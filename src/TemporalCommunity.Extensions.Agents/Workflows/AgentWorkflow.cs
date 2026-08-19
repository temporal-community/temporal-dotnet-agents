using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Drives the durable-agent dispatch loop: each LLM call is a separate <c>RunDurableAgentStep</c>
/// activity, and each tool call is a separate <c>InvokeAgentTool</c> activity dispatched in
/// parallel via <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
/// </summary>
[Workflow("TemporalCommunity.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow :
    DurableChatWorkflowBase<AgentResponse>,
    TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
{
    internal static readonly SearchAttributeKey<string> AgentNameSearchAttribute =
        SearchAttributeKey.CreateKeyword("AgentName");

    /// <summary>
    /// Default StateBag size threshold (in bytes) for the continue-as-new warning.
    /// Warns when the serialized <c>CarriedStateBag</c> exceeds 64 KB. Warning only —
    /// no hard failure — so sessions keep running even when StateBag grows large.
    /// This constant is <c>internal</c> so tests can reference it without magic numbers.
    /// </summary>
    internal const int StateBagSizeWarnThresholdBytes = 64 * 1024;

    // MAF-specific input (typed view of the base's Input). Set in RunAsync.
    private AgentWorkflowInput? _input;

    // MAF's complete approval decisions. The shared base owns the core decision archive; this
    // list carries the additional scope identity in the same order and with the same retention
    // bound. A pending entry is held separately until the request completes so eviction occurs
    // only when both archives record the final resolution.
    private const int MaxRetainedAgentApprovalResolutions = 32;
    private readonly List<DurableAgentApprovalDecision> _resolvedAgentApprovals = [];
    private DurableAgentApprovalDecision? _pendingAgentApprovalDecision;
    private DurableAgentApprovalDecision? _resolvingAgentApprovalDecision;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    // Item 9 (F1 + F3 content-hash gate): tracks the raw-text hash of the last StateBag
    // value we serialized into an activity input. When the bag hasn't changed between
    // dispatches we pass null instead of re-sending the full JSON, which at 64 KB × N
    // activities would add ~700 KB of redundant history bytes per iteration.
    //
    // Determinism: we use StableHash(rawText) — a fixed-seed FNV-1a 32-bit hash — to detect
    // whether the bag changed between dispatches. We deliberately do NOT use string.GetHashCode()
    // because .NET randomizes it per-process to prevent hash-flooding; two runs of the workflow
    // would compute different hashes for identical bytes, making the "did it change?" comparison
    // diverge and producing a NonDeterministicWorkflowException in rare hash-collision cases.
    //
    // The hash is a workflow-thread local variable that is NEVER stored in Temporal history and
    // never crosses a process boundary — it is reset on every CAN (line ~948). The only risk
    // is a collision (probability ~1/2^32 per turn-pair); using a stable hash makes that risk
    // consistent across replay runs instead of process-seed-dependent.
    //
    // Activities receiving null where they previously received a bag must behave identically
    // to receiving the unchanged bag. The merge target (_currentStateBag) is always authoritative;
    // passing null just avoids re-serializing unchanged bytes.
    private int? _lastSentStateBagHash;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _currentStateBag = input.CarriedStateBag;
        RestoreResolvedAgentApprovals(input.AgentApprovalResolutionHistory);

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);

        await base.RunAsync(input).ConfigureAwait(true);
    }

    /// <summary>
    /// Validates that a <see cref="RunAgentAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RunAgentAsync))]
    public void ValidateRunAgent(RunRequest request)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (request?.Messages is null || request.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Runs the agent with the given request and returns the response.
    /// Updates are serialized — only one runs at a time.
    /// </summary>
    [WorkflowUpdate("Run")]
    public async Task<AgentResponse> RunAgentAsync(RunRequest request)
    {
        var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);

        var (output, _) = await RunTurnAsync(requestEntry, chatOptions: null);

        Workflow.Logger.LogWorkflowUpdateCompleted(
            _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
        return output;
    }

    /// <summary>
    /// Queues a fire-and-forget run. The workflow does not wait for this to complete.
    /// </summary>
    [WorkflowSignal("RunFireAndForget")]
    public Task RunAgentFireAndForgetAsync(RunRequest request)
    {
        _ = ProcessFireAndForgetAsync(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a pending agent approval with optional MAF-only reusable-scope semantics.
    /// </summary>
    internal async Task<DurableApprovalResolutionResult> ResolveAgentApprovalAsync(
        DurableAgentApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // The base handler waits for its own input initialization. AgentWorkflow's typed ledger
        // has a parallel carried input, so initialize it before comparing an early update retry.
        if (_input is null)
        {
            await Workflow.WaitConditionAsync(() => _input is not null).ConfigureAwait(true);
        }

        if (_resolvedAgentApprovals.Count == 0
            && _input!.AgentApprovalResolutionHistory is { Count: > 0 } carriedResolutions)
        {
            RestoreResolvedAgentApprovals(carriedResolutions);
        }

        var coreDecision = new DurableApprovalDecision
        {
            RequestId = decision.RequestId,
            Approved = decision.Approved,
            Reason = decision.Reason,
        };

        _resolvingAgentApprovalDecision = decision;
        try
        {
            var genericResult = await base.ResolveApprovalAsync(coreDecision).ConfigureAwait(true);
            if (genericResult.Status == DurableApprovalResolutionStatus.Accepted)
            {
                return genericResult;
            }

            var knownDecision = FindKnownAgentApproval(decision.RequestId);
            return knownDecision is null
                ? genericResult
                : CreateResolutionResult(
                    IsEquivalent(knownDecision, decision)
                        ? DurableApprovalResolutionStatus.AlreadyResolved
                        : DurableApprovalResolutionStatus.Conflict,
                    decision.RequestId);
        }
        finally
        {
            _resolvingAgentApprovalDecision = null;
        }
    }

    /// <summary>
    /// Privileged update used only by the opt-in session-scope administration service.
    /// </summary>
    [WorkflowUpdate("GrantSessionApprovalScope")]
    public async Task<SessionApprovalScopeGrantResult> GrantSessionApprovalScopeAsync(
        SessionApprovalScopeGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_input is null)
        {
            await Workflow.WaitConditionAsync(() => _input is not null).ConfigureAwait(true);
        }

        if (_input!.UseApprovalScopes != true)
        {
            throw ScopeAdministrationFailure(
                "This agent is not configured for reusable session approval scopes.");
        }
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw ScopeAdministrationFailure("RequestId is required.");
        }
        if ((request.Pattern is null) == !request.MatchAllArguments)
        {
            throw ScopeAdministrationFailure(
                "Specify exactly one of Pattern or MatchAllArguments.");
        }
        if (request.ExpiresAt <= Workflow.UtcNow)
        {
            throw ScopeAdministrationFailure("ExpiresAt must be later than workflow time.");
        }

        var known = FindKnownAgentApproval(request.RequestId);
        var grantId = known?.GrantId ?? Workflow.NewGuid().ToString("N");
        var decision = new DurableAgentApprovalDecision
        {
            RequestId = request.RequestId,
            Approved = true,
            Reason = request.Reason,
            Scope = ApprovalScope.Session,
            ScopePattern = request.Pattern,
            GrantId = grantId,
            MatchAllArguments = request.MatchAllArguments,
            ExpiresAt = request.ExpiresAt,
            Actor = request.Actor,
        };

        var resolution = await ResolveAgentApprovalAsync(decision).ConfigureAwait(true);
        return new SessionApprovalScopeGrantResult
        {
            Resolution = resolution,
            GrantId = resolution.Status is DurableApprovalResolutionStatus.Accepted
                or DurableApprovalResolutionStatus.AlreadyResolved
                ? grantId
                : null,
        };
    }

    /// <summary>Revokes one reusable session grant by stable grant ID.</summary>
    [WorkflowUpdate("RevokeSessionApprovalScope")]
    public Task<bool> RevokeSessionApprovalScopeAsync(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            throw ScopeAdministrationFailure("GrantId is required.");
        }

        var (updatedStateBag, removed) = ApprovalScopeCoordinator.RevokeSessionScopeFromStateBag(
            _currentStateBag,
            grantId);
        _currentStateBag = updatedStateBag;
        return Task.FromResult(removed);
    }

    private static ApplicationFailureException ScopeAdministrationFailure(string message) =>
        new(message, errorType: "TemporalAgentApprovalScopeInvalidRequest", nonRetryable: true);

    // ── Hooks supplied to the base class ────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnApprovalResolutionAccepted(DurableApprovalDecision decision)
    {
        // The generic update is a shared-dashboard path and therefore has no scope. When the
        // typed update entered the base state machine, preserve its complete MAF identity
        // instead. The entry is finalized only from OnApprovalRequestResolved so both retained
        // archives add and evict the same request IDs together.
        _pendingAgentApprovalDecision = _resolvingAgentApprovalDecision is { } typed
            && string.Equals(typed.RequestId, decision.RequestId, StringComparison.Ordinal)
            ? typed
            : CreateThisCallOnlyDecision(decision);
    }

    /// <inheritdoc/>
    protected override void OnApprovalRequestResolved(DurableApprovalDecision decision)
    {
        var resolvedDecision = _pendingAgentApprovalDecision is { } pending
            && string.Equals(pending.RequestId, decision.RequestId, StringComparison.Ordinal)
            ? pending
            : CreateThisCallOnlyDecision(decision);

        RememberResolvedAgentApproval(resolvedDecision);
        _pendingAgentApprovalDecision = null;
    }

    /// <inheritdoc/>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        AgentResponse output,
        DateTimeOffset createdAt) =>
        AgentSessionResponse.FromAgentResponse(correlationId, output, createdAt);

    /// <inheritdoc/>
    protected override Task<List<DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ReduceHistoryByKeyAsync(
                new TemporalCommunity.Extensions.AI.ReduceHistoryByKeyInput
                {
                    ReducerKey = reducerKey,
                    History = history,
                }),
            activityOptions);

    /// <inheritdoc/>
    protected override Task<AgentResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // This path builds its step options from the agent's frozen workflow input so per-agent
        // settings are applied consistently across every step in the durable agent loop.
        _ = activityOptions;
        var agentRequestEntry = (AgentSessionRequest)requestEntry;
        var runRequest = ToRunRequest(agentRequestEntry);

        return ExecuteAgentTurnAsync(runRequest);
    }

    /// <inheritdoc/>
    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(_input);

        // `with` copies all properties from _input and overrides the fields that differ
        // for the new run: the base class CAN fields (carried from the base input parameter)
        // plus the StateBag snapshot. RetryPolicy and ResolvedWorkerConfig carry forward
        // unchanged from _input.
        var carriedInput = _input with
        {
            CarriedStateBag = _currentStateBag,
            // Base class CAN fields — sourced from the base DurableChatWorkflowInput arg.
            TimeToLive = input.TimeToLive,
            CarriedHistory = input.CarriedHistory,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducer = input.HistoryReducer,
            HistoryReducerKey = input.HistoryReducerKey,
            OriginalCreatedAt = input.OriginalCreatedAt,
            ActivityTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            AgentApprovalResolutionHistory = _resolvedAgentApprovals.ToList(),
        };

        // StateBag size guard (Feature D): emit a warning when the serialized StateBag
        // exceeds the configurable threshold (default 64 KB). The warning only — no hard
        // failure — so sessions keep running even when StateBag grows large.
        if (_currentStateBag.HasValue)
        {
            var stateBagJson = _currentStateBag.Value.GetRawText();
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(stateBagJson);
            if (byteCount > StateBagSizeWarnThresholdBytes)
            {
                Workflow.Logger.LogWarning(
                    "[{SessionId}] CarriedStateBag is {Bytes:N0} bytes at continue-as-new time " +
                    "(threshold: {Threshold:N0} bytes). Consider reducing AIContextProvider state " +
                    "to avoid bloating the CAN payload.",
                    Workflow.Info.WorkflowId, byteCount, StateBagSizeWarnThresholdBytes);
            }
        }

        Workflow.Logger.LogWorkflowContinueAsNew(
            _input.AgentName, Workflow.Info.WorkflowId,
            input.CarriedHistory?.Count ?? 0);

        return Workflow.CreateContinueAsNewException(
            (AgentWorkflow wf) => wf.RunAsync(carriedInput));
    }

    /// <inheritdoc/>
    protected override void UpsertCustomSearchAttributes()
    {
        if (_input is not null)
        {
            Workflow.UpsertTypedSearchAttributes(
                AgentNameSearchAttribute.ValueSet(_input.AgentName));
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs the original <see cref="RunRequest"/> from a stored
    /// <see cref="AgentSessionRequest"/>.
    /// </summary>
    private static RunRequest ToRunRequest(AgentSessionRequest entry)
    {
        ChatResponseFormat? responseFormat = null;
        if (string.Equals(entry.ResponseType, "json", StringComparison.OrdinalIgnoreCase))
        {
            responseFormat = entry.ResponseSchema is { } schema
                ? ChatResponseFormat.ForJsonSchema(schema)
                : ChatResponseFormat.Json;
        }

        return new RunRequest(
            entry.Messages.ToList(),
            responseFormat: responseFormat,
            enableToolCalls: entry.EnableToolCalls,
            enableToolNames: entry.EnableToolNames?.ToList())
        {
            CorrelationId = entry.CorrelationId,
            OrchestrationId = entry.OrchestrationId,
        };
    }

    private async Task<AgentResponse> ExecuteAgentTurnAsync(RunRequest runRequest)
    {
        var stepActivityOptions = new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
            RetryPolicy = _input!.RetryPolicy,
        };

        return await ExecuteDurableAgentTurnAsync(runRequest, stepActivityOptions).ConfigureAwait(true);
    }

    /// <summary>
    /// Durable-agent dispatch loop. Drives the alternation between
    /// <c>RunDurableAgentStepAsync</c> (one LLM call) and <c>InvokeAgentToolAsync</c> per tool
    /// call. Tool calls within a single LLM response fan out via
    /// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>; the loop
    /// terminates when the LLM returns a final assistant message or when
    /// <see cref="DurableAgentBuilder.MaxToolCallsPerTurn"/> iterations are exceeded.
    /// </summary>
    private async Task<AgentResponse> ExecuteDurableAgentTurnAsync(
        RunRequest runRequest,
        ActivityOptions stepActivityOptions)
    {
        Workflow.Logger.LogDurableAgentTurnStarted(_input!.AgentName, Workflow.Info.WorkflowId);

        var accumulated = FlattenHistoryMessages();

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;

        // Note: do NOT snapshot _input.MaxToolCallsPerTurn here. The resolution handshake
        // mutates _input mid-loop on proxy-started sessions, so we must re-read it each iteration
        // (and again after the loop for the aborted-response log/message).
        for (var iteration = 0; iteration < _input!.MaxToolCallsPerTurn; iteration++)
        {
            // Proxy-started sessions have WorkerSettingsResolved=false.
            // On the first step of the first turn, ask the activity to resolve worker-side
            // settings (per-tool activity options) and return them.
            var needsResolution = iteration == 0 && !_input!.WorkerSettingsResolved;

            // F1 optimization: pass the full bag on the first step of each turn (force=true),
            // and on subsequent steps only when the bag actually changed (hash gate).
            var bagForStep = GetStateBagForDispatch(force: iteration == 0);

            var stepInput = new AgentStepInput
            {
                AgentName = _input!.AgentName,
                Request = runRequest,
                AccumulatedMessages = accumulated,
                SerializedStateBag = bagForStep,
                SessionId = null,
                NeedsWorkerSettingsResolution = needsResolution,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            // Apply resolved worker-side settings once and carry the complete bundle forward
            // through continue-as-new.
            if (needsResolution && stepResult.ResolvedWorkerConfig is not null)
            {
                _input = _input! with
                {
                    CarriedStateBag = _currentStateBag,
                    ResolvedWorkerConfig = stepResult.ResolvedWorkerConfig,
                };
            }

            // Context providers run inside the LLM-step activity and are trusted-tier by design
            // (developer-registered, same trust as the workflow), so their StateBag output is
            // applied unfiltered here — unlike tool/interceptor write-backs, which are deny-list
            // filtered via StateBagMerge.
            //
            // Overlay (not replace) the activity's StateBag output on top of the carried
            // _currentStateBag. A replace loses workflow-thread writes (e.g. approval-scope records
            // written by WriteSessionScopeToStateBag between activities, or a context provider's
            // temporal.working_set) whenever a turn ends on a hash-gated LLM step that returns a
            // null/subset bag. The overlay is unfiltered here because context-provider output is
            // trusted-tier — see StateBagMerge.OverlayTrustedStateBag.
            _currentStateBag = StateBagMerge.OverlayTrustedStateBag(_currentStateBag, stepResult.UpdatedStateBag);

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.Add(stepResult.AssistantMessage);
            allTurnMessages.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                Workflow.Logger.LogDurableAgentTurnCompleted(_input!.AgentName, iteration + 1);
                var finalResponse = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                return finalResponse;
            }

            var toolCalls = stepResult.ToolCalls;

            var registeredToolNames = _input!.DurableAgentToolActivityOptions?.Keys.ToArray()
                ?? [];
            var enabledToolNames = runRequest.EnableToolNames is { } requestedNames
                ? requestedNames.ToArray()
                : null;
            var enabledToolCalls = new bool[toolCalls.Count];
            for (var i = 0; i < toolCalls.Count; i++)
            {
                enabledToolCalls[i] = AgentRunToolSelectionPolicy.IsCallEnabled(
                    toolCalls[i].Name,
                    registeredToolNames,
                    runRequest.EnableToolCalls,
                    enabledToolNames);
                if (!enabledToolCalls[i])
                {
                    Workflow.Logger.LogRunToolCallBlocked(
                        _input.AgentName,
                        Workflow.Info.WorkflowId,
                        runRequest.CorrelationId ?? string.Empty,
                        iteration + 1,
                        toolCalls[i].Name);
                }
            }

            Workflow.Logger.LogDurableAgentTurnIteration(_input!.AgentName, iteration + 1, toolCalls.Count);

            // ── Feature L — Phase 1: Fan out interceptor activities in parallel ────────────
            // Build interceptor results for all tool calls. Tools opted out (SkipInterceptor)
            // or when no interceptor is configured get a synthetic Proceed.
            DurableToolInterceptorResult[]? interceptorResults = null;
            var interceptorOpts = _input!.InterceptorActivityOptions;
            var interceptorToolOpts = _input!.InterceptorToolActivityOptions;
            var skippedTools = _input!.InterceptorSkippedTools;

            if (interceptorOpts is not null)
            {
                var interceptorTasks = new List<Task<DurableToolInterceptorResult>>(toolCalls.Count);
                // F1 optimization: all interceptors in a fan-out see the same bag snapshot.
                // Compute once here so the (non-forced) hash gate fires exactly once for the entire
                // fan-out (subsequent calls inside the loop would return null via the unchanged-hash
                // path).
                //
                // The scope-aware approval interceptor reads session-scope records straight from
                // the dispatched StateBag (RunToolInterceptorAsync is stateless — it has NO carried
                // bag to fall back to, unlike the LLM step). If the hash gate returns null here, the
                // interceptor sees an empty bag, cannot find an existing session-scope grant, and
                // re-prompts a tool that should auto-approve. So when any scope-aware tool is
                // registered, force the full bag for the interceptor fan-out; the F1 hash-gate
                // optimization only holds for consumers that can fall back to carried state.
                var forceInterceptorBag = _input!.ScopeAwareTools is { Count: > 0 };
                var bagForInterceptors = GetStateBagForDispatch(force: forceInterceptorBag);
                for (var i = 0; i < toolCalls.Count; i++)
                {
                    var tc = toolCalls[i];
                    if (!enabledToolCalls[i])
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed }));
                        continue;
                    }

                    if (DurableToolDecisionPolicy.IsToolSkipped(tc.Name, skippedTools))
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed }));
                    }
                    else
                    {
                        var interceptorInput = new DurableToolInterceptorInput
                        {
                            AgentName = _input!.AgentName,
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null
                                ? null
                                : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            SerializedStateBag = bagForInterceptors,
                            // Feature B: populate scope-aware fields so the interceptor can
                            // consult scope records and enforce the approval gate.
                            ScopeAware = _input!.ScopeAwareTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                            RequiresApproval = _input!.RequiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true
                                || _input!.ScopeAwareApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                            ApprovalEvaluationTime = Workflow.UtcNow,
                        };

                        // See also: DurableChatWorkflow.ExecutePattern3TurnAsync (MEAI path) — parallel typed dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(tc.Name, interceptorOpts, interceptorToolOpts)));
                    }
                }

                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);

                // X-2: merge any StateBag mutations the interceptors made back into the carried
                // bag, BEFORE tool dispatch, so a tool sees interceptor-driven state changes.
                // Interceptors fan out concurrently; merge in tool-call index order (later index
                // wins) for replay determinism — never by completion order.
                MergeStateBagWriteBacks(
                    [.. interceptorResults.Select(r => r?.UpdatedStateBag)]);
            }

            // ── Feature L — Phase 2 & Feature A: Process decisions, park for approvals ────
            //
            // Safety invariant (BLOCK-4): NO tool activity is dispatched until ALL approval
            // waits in this turn are fully resolved. Proceed-outcome tool inputs are buffered
            // in pendingToolDispatches and dispatched in Phase 2.5, after the approval loop
            // completes. This prevents a write-style tool (e.g. send_email, apply_refund) from
            // executing concurrently with a human review window opened by another tool in the
            // same turn.
            //
            var toolTasks = new Task<InvokeAgentToolResult>?[toolCalls.Count];
            var syntheticResults = new string?[toolCalls.Count]; // null = real tool result

            // Buffered dispatches: populated during Phase 2, dispatched in Phase 2.5.
            var pendingToolDispatches = new List<(int Index, InvokeAgentToolInput Input, ActivityOptions Options)>(toolCalls.Count);

            var requiresApprovalTools = _input!.RequiresApprovalTools;

            // StateBag starvation guard (Fix 1): a user tool on the Proceed path may read ANY
            // workflow-thread-written StateBag key — approval-scope records (scope-aware tools),
            // context-provider output (e.g. WorkingSetContextProvider's "temporal.working_set"),
            // or any custom key. InvokeAgentToolAsync is stateless: FromStateBag(id, null) builds
            // an EMPTY session, so a null dispatch (F1 hash gate, unchanged bag) starves the tool
            // on step 2+ of a hash-unchanged turn. Unlike the interceptor (only scope records
            // matter → force gated on ScopeAwareTools), there is NO clean workflow-side signal for
            // "context providers registered" — ContextProviderFactories live on the activity-side
            // registration, never serialized into ProxyResolvedWorkerConfig. So we always force the
            // Proceed-path tool bag: correctness over the marginal F1 saving. Tool calls are far
            // rarer than LLM steps and already do real activity work, so the extra bag payload is
            // negligible. Computed ONCE here and shared across the whole Proceed fan-out (do not
            // re-serialize per tool). The approved/PauseForApproval path (~L756) already forces.
            var bagForProceedTools = GetStateBagForDispatch(force: true);

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (!enabledToolCalls[i])
                {
                    syntheticResults[i] = AgentRunToolSelectionPolicy.CreateBlockedResult(tc.Name);
                    continue;
                }

                var interceptorResult = interceptorResults?[i];

                // Determine effective outcome (Rule 2: RequireApproval floor, Block never overridden).
                var outcome = DurableToolDecisionPolicy.GetEffectiveOutcome(
                    interceptorResult?.Outcome, tc.Name, requiresApprovalTools);

                switch (outcome)
                {
                    case DurableToolOutcome.Proceed:
                        // Buffer for dispatch after all approval waits resolve (BLOCK-4).
                        pendingToolDispatches.Add((i, new InvokeAgentToolInput
                        {
                            AgentName = _input!.AgentName,
                            ToolName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            CallId = tc.CallId,
                            // Forced above (bagForProceedTools) — a Proceed tool may read any
                            // workflow-thread StateBag key; null-gating would starve it. Shared
                            // across the whole fan-out, computed once.
                            SerializedStateBag = bagForProceedTools,
                        }, ResolveDurableToolActivityOptions(tc.Name)));
                        break;

                    case DurableToolOutcome.PauseForApproval:
                        // Feature A: park the turn loop; wait for a human decision via
                        // the DurableApprovalMixin (compute-free durable wait).
                        var approvalRequest = new DurableApprovalRequest
                        {
                            RequestId = $"{tc.CallId ?? tc.Name}-{Workflow.NewGuid():N}",
                            FunctionName = tc.Name,
                            CallId = tc.CallId,
                            Description = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, tc.Name),
                            // Metadata is deliberately interceptor-authored. Do not expose raw
                            // model function arguments to a reviewer unless an interceptor has
                            // first reduced them to explicit, safe review data.
                            ReviewData = interceptorResult?.Metadata,
                        };

                        // Sequential: the mixin enforces one pending approval at a time.
                        // Feature A: we call the protected turn-loop path, which parks this
                        // workflow fiber (compute-free) without blocking inside an activity.
                        var decision = await RequestApprovalFromTurnLoopAsync(
                            approvalRequest,
                            _input!.ApprovalTimeout,
                            onRequested: req => Workflow.Logger.LogInformation(
                                "[{SessionId}] Approval requested for tool '{ToolName}' (RequestId: {RequestId})",
                                Workflow.Info.WorkflowId, req.FunctionName, req.RequestId),
                            onResolved: dec => Workflow.Logger.LogInformation(
                                "[{SessionId}] Approval resolved for tool '{ToolName}' (RequestId: {RequestId}, Approved: {Approved})",
                                Workflow.Info.WorkflowId, approvalRequest.FunctionName, dec.RequestId, dec.Approved))
                            .ConfigureAwait(true);

                        if (decision.Approved)
                        {
                            // Feature B — Task 6.6: persist scope before dispatching the tool.
                            var agentDecision = FindKnownAgentApproval(decision.RequestId)
                                ?? CreateThisCallOnlyDecision(decision);
                            var scope = NormalizeApprovalScopeForPersistence(agentDecision);
                            var isScopeAwareTool = _input!.ScopeAwareTools?.Contains(
                                tc.Name, StringComparer.OrdinalIgnoreCase) == true;


                            if (scope == ApprovalScope.Session && isScopeAwareTool)
                            {
                                // Write session-scope record (pure workflow-thread, no I/O).
                                _currentStateBag = ApprovalScopeCoordinator.WriteSessionScopeToStateBag(
                                    _currentStateBag,
                                    tc.Name,
                                    agentDecision.ScopePattern,
                                    agentDecision.MatchAllArguments,
                                    agentDecision.GrantId!,
                                    agentDecision.ExpiresAt!.Value,
                                    agentDecision.Actor,
                                    agentDecision.Reason,
                                    decision.RequestId,
                                    Workflow.UtcNow,
                                    _input!.MaxAlwaysScopeCacheRecords,
                                    _input!.MaxAlwaysScopeCacheBytes,
                                    Workflow.Info.WorkflowId,
                                    Workflow.Logger);
                            }
                            else if (scope == ApprovalScope.Session && !isScopeAwareTool)
                            {
                                Workflow.Logger.LogWarning(
                                    "[{SessionId}] Approval scope '{Scope}' requested for non-scope-aware tool '{ToolName}'. " +
                                    "The scope was ignored. Register the tool with ScopeAware() to persist reusable approvals.",
                                    Workflow.Info.WorkflowId, scope, tc.Name);
                                scope = ApprovalScope.ThisCallOnly;
                            }

                            // Buffer the approved tool for dispatch in Phase 2.5.
                            // F1 optimization: after WriteSessionScopeToStateBag the bag may
                            // have changed (new scope record written), so force=true to ensure
                            // the approved tool sees the updated state. The hash is refreshed.
                            pendingToolDispatches.Add((i, new InvokeAgentToolInput
                            {
                                AgentName = _input!.AgentName,
                                ToolName = tc.Name,
                                Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                                CallId = tc.CallId,
                                // Scope record may have been written above — always send the bag.
                                SerializedStateBag = GetStateBagForDispatch(force: true),
                            }, ResolveDurableToolActivityOptions(tc.Name)));
                        }
                        else
                        {
                            // Denied or timed out — inject an error result.
                            var denialReason = string.IsNullOrEmpty(decision.Reason)
                                ? "Tool call was denied or timed out."
                                : decision.Reason;
                            syntheticResults[i] = DurableToolDecisionPolicy.DenialMessage(denialReason);
                        }
                        break;

                    case DurableToolOutcome.Skip:
                        syntheticResults[i] = DurableToolDecisionPolicy.SkipMessage(interceptorResult?.Message);
                        break;

                    case DurableToolOutcome.Block:
                    default:
                        syntheticResults[i] = DurableToolDecisionPolicy.BlockMessage(interceptorResult?.Message);
                        break;
                }
            }

            // ── Phase 2.5: Dispatch all buffered tool activities ──────────────────────────
            // All approval waits are resolved before any InvokeAgentTool activity starts.
            foreach (var (idx, input, opts) in pendingToolDispatches)
            {
                toolTasks[idx] = Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.InvokeAgentToolAsync(input),
                    opts);
            }

            // ── Phase 3: Wait for all dispatched tool activities ──────────────────────────
            var pendingTasks = toolTasks
                .Where(t => t is not null)
                .Cast<Task<InvokeAgentToolResult>>()
                .ToList();
            InvokeAgentToolResult[]? toolResults = pendingTasks.Count > 0
                ? await Workflow.WhenAllAsync(pendingTasks).ConfigureAwait(true)
                : null;

            // Assemble final results in original order.
            // X-1: also collect each tool's StateBag write-back, slotted by tool-call index so
            // the post-fan-out merge is deterministic (later index wins) regardless of which
            // activity completed first. toolResults is in ascending tool-call-index order
            // (pendingTasks was built by iterating toolTasks[] in index order).
            var functionResultContents = new List<AIContent>(toolCalls.Count);
            var toolStateBagWriteBacks = new JsonElement?[toolCalls.Count];
            var pendingIdx = 0;
            for (var i = 0; i < toolCalls.Count; i++)
            {
                if (syntheticResults[i] is { } synthetic)
                {
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: synthetic));
                }
                else if (toolResults is not null && pendingIdx < toolResults.Length)
                {
                    var toolResult = toolResults[pendingIdx++];
                    toolStateBagWriteBacks[i] = toolResult.UpdatedStateBag;
                    // S-X-6: toolResult.Result crosses the activity boundary as a JsonElement
                    // (declared object?), so FunctionResultContent.Result holds a JsonElement here,
                    // not the tool's domain type. Accepted limitation — see InvokeAgentToolResult.Result.
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResult.Result));
                }
            }

            // X-1: merge tool StateBag mutations back in tool-call index order. The merge is
            // post-result and does NOT re-run any tool, so .NoRetry() write tools are not
            // double-executed by this step.
            MergeStateBagWriteBacks(toolStateBagWriteBacks);

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
        }

        var effectiveMaxIterations = _input!.MaxToolCallsPerTurn;
        Workflow.Logger.LogDurableAgentTurnAborted(_input!.AgentName, effectiveMaxIterations);

        var errorMessage = new ChatMessage(
            ChatRole.Assistant,
            $"Maximum tool-call iterations ({effectiveMaxIterations}) exceeded for agent '{_input!.AgentName}'. " +
            "The agent did not converge on a final answer.");
        allTurnMessages.Add(errorMessage);

        var abortedResponse = new AgentResponse
        {
            Messages = allTurnMessages,
            Usage = totalUsage,
            CreatedAt = Workflow.UtcNow,
        };

        return abortedResponse;
    }

    private void RestoreResolvedAgentApprovals(
        IReadOnlyList<DurableAgentApprovalDecision>? decisions)
    {
        _resolvedAgentApprovals.Clear();
        if (decisions is null)
        {
            return;
        }

        foreach (var decision in decisions.TakeLast(MaxRetainedAgentApprovalResolutions))
        {
            _resolvedAgentApprovals.Add(decision);
        }
    }

    private void RememberResolvedAgentApproval(DurableAgentApprovalDecision decision)
    {
        _resolvedAgentApprovals.RemoveAll(existing =>
            string.Equals(existing.RequestId, decision.RequestId, StringComparison.Ordinal));
        _resolvedAgentApprovals.Add(decision);
        if (_resolvedAgentApprovals.Count > MaxRetainedAgentApprovalResolutions)
        {
            _resolvedAgentApprovals.RemoveRange(
                0,
                _resolvedAgentApprovals.Count - MaxRetainedAgentApprovalResolutions);
        }
    }

    private DurableAgentApprovalDecision? FindKnownAgentApproval(string requestId)
    {
        if (_pendingAgentApprovalDecision is { } pending
            && string.Equals(pending.RequestId, requestId, StringComparison.Ordinal))
        {
            return pending;
        }

        return _resolvedAgentApprovals.LastOrDefault(existing =>
            string.Equals(existing.RequestId, requestId, StringComparison.Ordinal));
    }

    private static DurableAgentApprovalDecision CreateThisCallOnlyDecision(
        DurableApprovalDecision decision) =>
        new()
        {
            RequestId = decision.RequestId,
            Approved = decision.Approved,
            Reason = decision.Reason,
            Scope = ApprovalScope.ThisCallOnly,
            ScopePattern = null,
        };

    private static bool IsEquivalent(
        DurableAgentApprovalDecision left,
        DurableAgentApprovalDecision right) =>
        left.Approved == right.Approved
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.Scope == right.Scope
        && AreEquivalent(left.ScopePattern, right.ScopePattern)
        && string.Equals(left.GrantId, right.GrantId, StringComparison.Ordinal)
        && left.MatchAllArguments == right.MatchAllArguments
        && left.ExpiresAt == right.ExpiresAt
        && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal);

    private static bool AreEquivalent(ApprovalScopePattern? left, ApprovalScopePattern? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.Type == right.Type
            && string.Equals(left.Parameter, right.Parameter, StringComparison.Ordinal)
            && string.Equals(left.Pattern, right.Pattern, StringComparison.Ordinal));

    private static DurableApprovalResolutionResult CreateResolutionResult(
        DurableApprovalResolutionStatus status,
        string requestId) =>
        new()
        {
            RequestId = requestId,
            Status = status,
        };

    /// <summary>
    /// Normalizes the <see cref="ApprovalScope"/> from an approved
    /// <see cref="DurableAgentApprovalDecision"/>, applying all validation rules.
    /// Returns <see cref="ApprovalScope.ThisCallOnly"/> for any invalid input.
    /// Delegates to <see cref="ApprovalScopeCoordinator.EvaluateScopeNormalization"/> and logs
    /// the degradation reason when applicable.
    /// </summary>
    private ApprovalScope NormalizeApprovalScopeForPersistence(DurableAgentApprovalDecision decision)
    {
        var (result, reason) = ApprovalScopeCoordinator.EvaluateScopeNormalization(decision);
        if (result == ApprovalScope.ThisCallOnly && reason is not null)
        {
            Workflow.Logger.LogWarning(
                "[{SessionId}] {Reason} Treating as ThisCallOnly.",
                Workflow.Info.WorkflowId, reason);
        }

        return result;
    }

    /// <summary>
    /// Deterministically merges a sequence of untrusted tool/interceptor StateBag write-backs into
    /// <c>_currentStateBag</c> in tool-call index order (later index wins). Delegates to
    /// <see cref="StateBagMerge.Merge"/>, which applies the reserved approval-scope deny-list
    /// (<see cref="StateBagMerge.ApprovalScopesReservedPrefix"/> + the agent's configured
    /// always-scopes store key) so a write-back can never forge or clobber an approval grant.
    /// See that type for the full merge policy (X-1 / X-2) and security rationale.
    /// </summary>
    private void MergeStateBagWriteBacks(IReadOnlyList<JsonElement?> updatedBags) =>
        _currentStateBag = StateBagMerge.Merge(
            _currentStateBag,
            updatedBags,
            _input!.AlwaysScopesStoreKey,
            Workflow.Logger);

    /// <summary>
    /// Returns the current StateBag for dispatch into an activity input, applying the
    /// content-hash gate (Item 9 / F1 + F3). When the bag hasn't changed since the last
    /// dispatch, returns <see langword="null"/> so we avoid re-serializing the full JSON
    /// into the Temporal event log.
    /// <para>
    /// <b>Two-tier consumer contract — read this before adding a new dispatch site.</b>
    /// There is NO carried-state fallback. A null return means "the bag is unchanged since the
    /// last dispatch"; an activity that receives null and reads the bag builds an EMPTY session
    /// via <c>TemporalAgentSession.FromStateBag(id, null)</c>. Consumers therefore split into two
    /// tiers:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Stateful / forced consumers</b> — read workflow-thread StateBag state, so they MUST pass
    /// <c>force: true</c> or they will silently see an empty bag on a hash-unchanged step:
    /// the LLM step at turn start (<c>force: iteration == 0</c>); the scope interceptor fan-out when
    /// scope-aware tools are present; and the <c>InvokeAgentTool</c> dispatch (both the Proceed path,
    /// which always forces because a user tool may read any workflow-written key, and the
    /// approved/PauseForApproval path, which forces after writing a scope record).
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="force">
    /// Pass <see langword="true"/> whenever the consuming activity reads workflow-thread StateBag
    /// state, to guarantee it receives the full bag even if the hash matches (see the two-tier
    /// contract above).
    /// </param>
    private JsonElement? GetStateBagForDispatch(bool force = false)
    {
        if (_currentStateBag is null)
        {
            _lastSentStateBagHash = null;
            return null;
        }

        var rawText = _currentStateBag.Value.GetRawText();
        var hash = StableHash(rawText);

        if (!force && _lastSentStateBagHash.HasValue && _lastSentStateBagHash.Value == hash)
        {
            // Bag unchanged since last dispatch — omit it (hash gate) to avoid re-serializing the
            // full JSON into the event log. IMPORTANT: there is NO carried-state fallback. An
            // activity that receives null and reads the bag builds an EMPTY session via
            // FromStateBag(id, null). Only correct for consumers that DON'T read workflow-thread
            // StateBag state. Any consumer that does read it MUST pass force: true (see the
            // two-tier contract on the method summary).
            return null;
        }

        _lastSentStateBagHash = hash;
        return _currentStateBag;
    }

    /// <summary>
    /// FNV-1a 32-bit hash — fixed-seed, deterministic across process restarts.
    /// Used for StateBag change detection. We cannot use <see cref="string.GetHashCode"/>
    /// because .NET randomizes it per-process (hash-flooding protection); a collision
    /// between two different bags under one seed but not another would cause the
    /// null-vs-full-bag dispatch decision to diverge on replay and produce a
    /// <c>WorkflowNondeterminismException</c>.
    /// </summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            const int offsetBasis = -2128831035; // FNV-1a 32-bit offset basis
            const int prime = 16777619;          // FNV-1a 32-bit prime
            var hash = offsetBasis;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= prime;
            }
            return hash;
        }
    }

    private List<ChatMessage> FlattenHistoryMessages()
    {
        var totalMessageCount = 0;
        foreach (var entry in History)
        {
            totalMessageCount += entry.Messages.Count;
        }

        var messages = new List<ChatMessage>(totalMessageCount);
        foreach (var entry in History)
        {
            foreach (var m in entry.Messages)
            {
                messages.Add(m);
            }
        }

        return messages;
    }

    private ActivityOptions ResolveDurableToolActivityOptions(string toolName)
    {
        if (DurableToolDecisionPolicy.TryGetToolValue(
            _input!.DurableAgentToolActivityOptions,
            toolName,
            out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = toolName,
            RetryPolicy = _input!.RetryPolicy,
        };
    }

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        try
        {
            var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
            await RunTurnAsync(requestEntry, chatOptions: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogFireAndForgetActivityFailed(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId, ex);
            // Swallow — fire-and-forget errors must not crash the session.
        }
    }

    JsonElement? TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
        .CaptureTurnRollbackState() => _currentStateBag;

    void TemporalCommunity.Extensions.AI.Internal.IDurableTurnRollbackParticipant
        .RestoreTurnRollbackState(JsonElement? state)
    {
        // Application/provider/tool StateBag changes belong to the failed turn and must not
        // leak into a later update. Approval-scope records are different: they are committed
        // by independent approval updates while the turn is parked, so retain those reserved
        // records even though the surrounding turn failed.
        _currentStateBag = StateBagMerge.RestoreTurnOwnedState(
            state,
            _currentStateBag,
            _input?.AlwaysScopesStoreKey);

        // The dispatch hash may describe the now-discarded bag. Invalidate it so the next
        // activity receives the restored state rather than a hash-gated null payload.
        _lastSentStateBagHash = null;
    }
}
