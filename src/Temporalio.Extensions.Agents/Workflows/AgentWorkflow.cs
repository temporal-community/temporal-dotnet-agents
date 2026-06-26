using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Agents.Approvals;
using Temporalio.Extensions.Agents.Scheduling;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Tools;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Approvals;
using Temporalio.Extensions.AI.Session;
using Temporalio.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Drives the durable-agent dispatch loop: each LLM call is a separate <c>RunDurableAgentStep</c>
/// activity, and each tool call is a separate <c>InvokeAgentTool</c> activity dispatched in
/// parallel via <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
/// </summary>
[Workflow("Temporalio.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow : DurableChatWorkflowBase<AgentResponse>
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

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    // Feature B: tracks whether the always-scopes load has happened in this workflow run.
    // Resets automatically on each continue-as-new (new workflow instance). Not serialized.
    private bool _alwaysScopesLoadedThisRun;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _currentStateBag = input.CarriedStateBag;

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);

        // Detect "external history mode" from the resolved agent input — when ANY history
        // store is configured (worker default or per-agent override), the workflow strips
        // message payloads from history entries before adding them, and the activity is
        // responsible for loading/appending via IAgentHistoryStore.
        // The `useExternalStore` flag below is computed from a workflow-only signal we set
        // when reducing history for continue-as-new (see CreateContinueAsNewException).

        // External-store mode + HistoryReducer: the base throws ContinueAsNewException after
        // calling our CreateContinueAsNewException hook (which is synchronous, so it cannot
        // dispatch activities). Intercept the throw here to fire the ReduceHistoryInStoreAsync
        // activity before re-throwing, so the next workflow run sees a bounded store.
        try
        {
            await base.RunAsync(input).ConfigureAwait(true);
        }
        catch (ContinueAsNewException can) when (UseExternalStoreMode)
        {
            var reduceInput = new ReduceHistoryInStoreInput
            {
                AgentName = input.AgentName,
                SessionId = Workflow.Info.WorkflowId,
                MaxEntryCount = input.MaxEntryCount,
            };
            await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ReduceHistoryInStoreAsync(reduceInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = input.ActivityTimeout,
                    HeartbeatTimeout = input.HeartbeatTimeout,
                    Summary = AgentActivities.BuildActivitySummary(input.AgentName),
                    RetryPolicy = input.RetryPolicy,
                }).ConfigureAwait(true);
            _ = can;
            throw;
        }
    }

    /// <summary>
    /// Indicates whether this workflow is operating in external-history mode. The workflow side
    /// reads this from the resolved cached state on first dispatch. Until then it cannot be known
    /// (the activity composes the cache lazily); we therefore compute it from the activity's
    /// echoed value via the workflow input. The agent client populates this via
    /// <see cref="AgentWorkflowInput.UseExternalStoreMode"/> when starting the workflow.
    /// </summary>
    private bool UseExternalStoreMode => _input?.UseExternalStoreMode == true;

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

    // ── Hooks supplied to the base class ────────────────────────────────────

    /// <inheritdoc/>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        AgentResponse output,
        DateTimeOffset createdAt) =>
        AgentSessionResponse.FromAgentResponse(correlationId, output, createdAt);

    /// <inheritdoc/>
    protected override Task<AgentResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Signal to the reader that this override intentionally reconstructs activity options.
        // If DurableChatWorkflowBase.RunTurnAsync starts populating activityOptions.RetryPolicy
        // or other fields that AgentWorkflow also sets, this assert will fire in debug builds.
        Debug.Assert(activityOptions.RetryPolicy is null,
            "Base class now sets RetryPolicy on activityOptions — revisit AgentWorkflow.ExecuteTurnAsync.");
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

        var useExternalStore = _input.UseExternalStoreMode;

        var carriedInput = new AgentWorkflowInput
        {
            AgentName = _input.AgentName,
            TaskQueue = _input.TaskQueue,
            CarriedStateBag = _currentStateBag,
            RetryPolicy = _input.RetryPolicy,
            // Carry forward the entire resolved-worker-config bundle (or null if proxy-started
            // and not yet resolved). This replaces the flat MaxToolCallsPerTurn /
            // UseExternalStoreMode / DurableAgentToolActivityOptions / WorkerSettingsResolved
            // quartet as of the Step 3c.1 migration.
            ResolvedWorkerConfig = _input.ResolvedWorkerConfig,

            TimeToLive = input.TimeToLive,
            CarriedHistory = useExternalStore ? null : input.CarriedHistory,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducer = input.HistoryReducer,
            OriginalCreatedAt = input.OriginalCreatedAt,
            ActivityTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
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

    /// <inheritdoc/>
    protected override bool ShouldStripMessagesFromHistoryEntry() => UseExternalStoreMode;

    /// <inheritdoc/>
    protected override DurableSessionEntry StripMessagesFromEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry switch
        {
            AgentSessionRequest agentReq => new AgentSessionRequest
            {
                CorrelationId = agentReq.CorrelationId,
                CreatedAt = agentReq.CreatedAt,
                Messages = [],
                OrchestrationId = agentReq.OrchestrationId,
                ResponseType = agentReq.ResponseType,
                ResponseSchema = agentReq.ResponseSchema,
                AdditionalProperties = agentReq.AdditionalProperties,
            },
            AgentSessionResponse agentResp => new AgentSessionResponse
            {
                CorrelationId = agentResp.CorrelationId,
                CreatedAt = agentResp.CreatedAt,
                Messages = [],
                Usage = agentResp.Usage,
                AdditionalProperties = agentResp.AdditionalProperties,
            },
            _ => base.StripMessagesFromEntry(entry),
        };
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

        return new RunRequest(entry.Messages.ToList(), responseFormat: responseFormat)
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

        // External history mode: workflow does not retain message payloads in History entries
        // (ShouldStripMessagesFromHistoryEntry returns true). Seed the LLM with just the current
        // request's messages; the activity will load prior session history from the store on
        // the first step (IsFirstStep = true).
        var accumulated = UseExternalStoreMode
            ? new List<ChatMessage>(runRequest.Messages)
            : FlattenHistoryMessages();

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;

        // Note: do NOT snapshot _input.MaxToolCallsPerTurn here. The resolution handshake
        // mutates _input mid-loop on proxy-started sessions, so we must re-read it each iteration
        // (and again after the loop for the aborted-response log/message).
        for (var iteration = 0; iteration < _input!.MaxToolCallsPerTurn; iteration++)
        {
            // Proxy-started sessions have WorkerSettingsResolved=false.
            // On the first step of the first turn, ask the activity to resolve worker-side
            // settings (external-store mode, per-tool activity options) and return them.
            var needsResolution = iteration == 0 && !_input!.WorkerSettingsResolved;

            var stepInput = new AgentStepInput
            {
                AgentName = _input!.AgentName,
                Request = runRequest,
                AccumulatedMessages = accumulated,
                SerializedStateBag = _currentStateBag,
                SessionId = null,
                IsFirstStep = iteration == 0,
                NeedsWorkerSettingsResolution = needsResolution,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            // Apply resolved worker-side settings once and carry forward via CAN. The
            // entire resolved bundle travels as ProxyResolvedWorkerConfig — 
            // the flat MaxToolCallsPerTurn / UseExternalStoreMode / DurableAgentToolActivityOptions
            // fields are now forwarding computed properties on AgentWorkflowInput.
            if (needsResolution && stepResult.ResolvedWorkerConfig is not null)
            {
                _input = new AgentWorkflowInput
                {
                    AgentName = _input!.AgentName,
                    TaskQueue = _input!.TaskQueue,
                    CarriedStateBag = _currentStateBag,
                    RetryPolicy = _input!.RetryPolicy,
                    ResolvedWorkerConfig = stepResult.ResolvedWorkerConfig,

                    TimeToLive = _input!.TimeToLive,
                    CarriedHistory = _input!.CarriedHistory,
                    ApprovalTimeout = _input!.ApprovalTimeout,
                    EnableSearchAttributes = _input!.EnableSearchAttributes,
                    MaxEntryCount = _input!.MaxEntryCount,
                    HistoryReducer = _input!.HistoryReducer,
                    OriginalCreatedAt = _input!.OriginalCreatedAt,
                    ActivityTimeout = _input!.ActivityTimeout,
                    HeartbeatTimeout = _input!.HeartbeatTimeout,
                };
            }

            _currentStateBag = stepResult.UpdatedStateBag;

            // Feature B — Sub-section B: load always-scopes at proxy-start session start.
            // Guard: needsResolution AND all three approval-scope flags true.
            // Proxy-started sessions have ResolvedWorkerConfig == null at RunAsync and skip
            // Sub-section A; this is the corresponding injection point for them.
            // These two injection points are mutually exclusive within a single workflow run.
            // After a continue-as-new, the resolved config is carried in AgentWorkflowInput
            // so the next run enters Sub-section A instead.
            if (needsResolution &&
                _input!.UseApprovalScopes == true &&
                _input!.UseApprovalScopeStoreMode == true &&
                _input!.ApplyAlwaysScopesAtSessionStart == true)
            {
                try
                {
                    var loaded = await Workflow.ExecuteActivityAsync(
                        (AgentActivities a) => a.LoadAlwaysScopesAsync(new LoadAlwaysScopesInput
                        {
                            AgentName = _input!.AgentName,
                            StoreKey = _input!.AlwaysScopesStoreKey!,
                        }),
                        ApprovalScopeActivityOptions()).ConfigureAwait(true);

                    if (loaded?.Scopes is { Count: > 0 } &&
                        IsWithinAlwaysScopeCacheBudget(
                            loaded.Scopes,
                            _input!.MaxAlwaysScopeCacheRecords,
                            _input!.MaxAlwaysScopeCacheBytes))
                    {
                        MergeAlwaysScopesIntoStateBag(loaded.Scopes);
                    }
                }
                catch (ActivityFailureException ex) when (!IsActivityCancellation(ex))
                {
                    Workflow.Logger.LogWarning(
                        "[{SessionId}] LoadAlwaysScopesAsync failed after retries exhausted. Always-scope cache not " +
                        "populated. Scope-aware tools will require normal approval this session. {Exception}",
                        Workflow.Info.WorkflowId, ex);
                }
                finally
                {
                    _alwaysScopesLoadedThisRun = true;
                }
            }

            // Feature B — Sub-section A: load always-scopes at direct-start session start.
            // Guard: first step of the first turn (!needsResolution means direct-start or post-CAN),
            // not already loaded this run, and all three approval-scope flags true.
            // This path fires for direct-start workflows (WorkerSettingsResolved == true) where
            // the load cannot happen before base.RunAsync because that would create a window where
            // DurableChatWorkflowBase.Input is not yet set (causing RequiredInput failures when
            // the RunAgentAsync update handler fires between the first await and base.RunAsync).
            if (!needsResolution &&
                !_alwaysScopesLoadedThisRun &&
                iteration == 0 &&
                _input!.UseApprovalScopes == true &&
                _input!.UseApprovalScopeStoreMode == true &&
                _input!.ApplyAlwaysScopesAtSessionStart == true)
            {
                try
                {
                    var loaded = await Workflow.ExecuteActivityAsync(
                        (AgentActivities a) => a.LoadAlwaysScopesAsync(new LoadAlwaysScopesInput
                        {
                            AgentName = _input!.AgentName,
                            StoreKey = _input!.AlwaysScopesStoreKey!,
                        }),
                        ApprovalScopeActivityOptions()).ConfigureAwait(true);

                    if (loaded?.Scopes is { Count: > 0 } &&
                        IsWithinAlwaysScopeCacheBudget(
                            loaded.Scopes,
                            _input!.MaxAlwaysScopeCacheRecords,
                            _input!.MaxAlwaysScopeCacheBytes))
                    {
                        MergeAlwaysScopesIntoStateBag(loaded.Scopes);
                    }
                }
                catch (ActivityFailureException ex) when (!IsActivityCancellation(ex))
                {
                    Workflow.Logger.LogWarning(
                        "[{SessionId}] LoadAlwaysScopesAsync failed after retries exhausted. Always-scope cache not " +
                        "populated. Scope-aware tools will require normal approval this session. {Exception}",
                        Workflow.Info.WorkflowId, ex);
                }
                finally
                {
                    _alwaysScopesLoadedThisRun = true;
                }
            }

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

                // Fix 2 (P1-3): append the full turn to the external store. This captures all
                // messages accumulated during the turn (tool-call messages, tool-result messages,
                // and the final assistant message) rather than just the final assistant message.
                if (UseExternalStoreMode)
                {
                    await Workflow.ExecuteActivityAsync(
                        (AgentActivities a) => a.AppendAgentTurnAsync(new AppendAgentTurnInput
                        {
                            AgentName = _input!.AgentName,
                            SessionId = Workflow.Info.WorkflowId,
                            Request = runRequest,
                            TurnResponse = finalResponse,
                        }),
                        new ActivityOptions
                        {
                            StartToCloseTimeout = _input!.ActivityTimeout,
                            HeartbeatTimeout = _input!.HeartbeatTimeout,
                            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                            RetryPolicy = _input!.RetryPolicy,
                        }).ConfigureAwait(true);

                    // Step 6d: if the activity-side trigger evaluator flagged compaction,
                    // dispatch CompactHistory now — after the current turn has been appended
                    // to the store. Marker correlation ID is workflow-minted (deterministic
                    // under replay) so retries reproduce the same marker rather than
                    // double-writing.
                    if (stepResult.CompactionNeeded &&
                        stepResult.CompactionTargetMessageIds is { Count: > 0 } targets)
                    {
                        var markerId = $"marker-{Workflow.NewGuid():N}";
                        await Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.CompactHistoryAsync(new CompactHistoryInput
                            {
                                AgentName = _input!.AgentName,
                                SessionId = Workflow.Info.WorkflowId,
                                TargetMessageIds = targets,
                                MarkerCorrelationId = markerId,
                            }),
                            new ActivityOptions
                            {
                                StartToCloseTimeout = _input!.ActivityTimeout,
                                HeartbeatTimeout = _input!.HeartbeatTimeout,
                                Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                                RetryPolicy = _input!.RetryPolicy,
                            }).ConfigureAwait(true);
                    }
                }

                return finalResponse;
            }

            var toolCalls = stepResult.ToolCalls;

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
                foreach (var tc in toolCalls)
                {
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
                            SerializedStateBag = _currentStateBag,
                            // Feature B: populate scope-aware fields so the interceptor can
                            // consult scope records and enforce the approval gate.
                            ScopeAware = _input!.ScopeAwareTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
                            RequiresApproval = _input!.RequiresApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true
                                || _input!.ScopeAwareApprovalTools?.Contains(tc.Name, StringComparer.OrdinalIgnoreCase) == true,
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

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
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
                            // X-1: seed the tool with accumulated session state (was null before).
                            SerializedStateBag = _currentStateBag,
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
                            var scope = NormalizeApprovalScopeForPersistence(decision);
                            var isScopeAwareTool = _input!.ScopeAwareTools?.Contains(
                                tc.Name, StringComparer.OrdinalIgnoreCase) == true;

                            if ((scope == ApprovalScope.Session || scope == ApprovalScope.Always) && isScopeAwareTool)
                            {
                                // Write session-scope record (pure workflow-thread, no I/O).
                                WriteSessionScopeToStateBag(tc.Name, decision.ScopePattern, decision.RequestId);
                            }
                            else if ((scope == ApprovalScope.Session || scope == ApprovalScope.Always) && !isScopeAwareTool)
                            {
                                Workflow.Logger.LogWarning(
                                    "[{SessionId}] Approval scope '{Scope}' requested for non-scope-aware tool '{ToolName}'. " +
                                    "The scope was ignored. Register the tool with ScopeAware() to persist reusable approvals.",
                                    Workflow.Info.WorkflowId, scope, tc.Name);
                                scope = ApprovalScope.ThisCallOnly;
                            }

                            if (scope == ApprovalScope.Always && _input!.UseApprovalScopeStoreMode == true)
                            {
                                // Dispatch the always-scope store activity before buffering the tool.
                                try
                                {
                                    await Workflow.ExecuteActivityAsync(
                                        (AgentActivities a) => a.AppendAlwaysScopeAsync(new AppendAlwaysScopeInput
                                        {
                                            AgentName = _input!.AgentName,
                                            SessionId = Workflow.Info.WorkflowId,
                                            StoreKey = _input!.AlwaysScopesStoreKey!,
                                            ToolName = tc.Name,
                                            Pattern = decision.ScopePattern,
                                            GrantedAt = Workflow.UtcNow,
                                            OriginatingRequestId = decision.RequestId,
                                        }),
                                        ApprovalScopeActivityOptions()).ConfigureAwait(true);
                                }
                                catch (ActivityFailureException ex) when (!IsActivityCancellation(ex))
                                {
                                    Workflow.Logger.LogWarning(
                                        "[{SessionId}] AppendAlwaysScopeAsync failed for tool '{ToolName}' after retries exhausted. " +
                                        "Always scope degraded to Session for this decision. The approved tool call will proceed. {Exception}",
                                        Workflow.Info.WorkflowId, tc.Name, ex);
                                    scope = ApprovalScope.Session;
                                }
                            }
                            else if (scope == ApprovalScope.Always && _input!.UseApprovalScopeStoreMode != true)
                            {
                                Workflow.Logger.LogWarning(
                                    "[{SessionId}] ApprovalScope.Always requested for tool '{ToolName}' but " +
                                    "approval-scope store mode is not enabled for this agent. Scope degraded to Session.",
                                    Workflow.Info.WorkflowId, tc.Name);
                            }

                            // Buffer the approved tool for dispatch in Phase 2.5.
                            pendingToolDispatches.Add((i, new InvokeAgentToolInput
                            {
                                AgentName = _input!.AgentName,
                                ToolName = tc.Name,
                                Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                                CallId = tc.CallId,
                                // X-1: seed with state including any session-scope record just
                                // written by WriteSessionScopeToStateBag above.
                                SerializedStateBag = _currentStateBag,
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

        // Fix 2 (P1-3): also append max-iteration turns. Previously isFinal was never true
        // when the cap was hit, so nothing was written to the external store.
        if (UseExternalStoreMode)
        {
            await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.AppendAgentTurnAsync(new AppendAgentTurnInput
                {
                    AgentName = _input!.AgentName,
                    SessionId = Workflow.Info.WorkflowId,
                    Request = runRequest,
                    TurnResponse = abortedResponse,
                }),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityTimeout,
                    HeartbeatTimeout = _input!.HeartbeatTimeout,
                    Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                    RetryPolicy = _input!.RetryPolicy,
                }).ConfigureAwait(true);
        }

        return abortedResponse;
    }

    // ── Feature B — approval-scope helper methods ──────────────────────────────

    /// <summary>
    /// Builds <see cref="ActivityOptions"/> for approval-scope store activities
    /// (<c>LoadAlwaysScopesAsync</c> and <c>AppendAlwaysScopeAsync</c>). Uses dedicated
    /// bounded timeout and retry policy from the resolved approval-scope options rather than
    /// the normal tool/LLM activity defaults.
    /// </summary>
    private ActivityOptions ApprovalScopeActivityOptions() => new ActivityOptions
    {
        StartToCloseTimeout = _input!.ApprovalScopeActivityTimeout,
        RetryPolicy = new Temporalio.Common.RetryPolicy
        {
            MaximumAttempts = _input!.ApprovalScopeActivityMaximumAttempts,
        },
        Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents an
    /// activity/workflow cancellation rather than a genuine store failure. Used as a
    /// <c>when</c> filter in the fail-open catch blocks so cancellation always propagates.
    /// </summary>
    /// <remarks>
    /// Deterministic: checks only <see cref="Workflow.CancellationToken"/> (workflow-thread
    /// signal) and the Temporal failure/cause chain (inert data).  Does not inspect wall-clock
    /// time, services, or any other non-deterministic state.
    /// </remarks>
    private static bool IsActivityCancellation(ActivityFailureException ex)
    {
        // Workflow-level cancellation: the token is already set before the exception surfaces.
        if (Workflow.CancellationToken.IsCancellationRequested)
        {
            return true;
        }

        // Activity-level cancellation: the SDK wraps the cancellation as a
        // CanceledFailureException in the InnerException chain of ActivityFailureException.
        return ex.InnerException is Temporalio.Exceptions.CanceledFailureException;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given <paramref name="scopes"/> fit within
    /// both the record-count and byte-size budgets for the always-scope StateBag cache.
    /// </summary>
    /// <remarks>
    /// Deterministic: serializes using <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>
    /// (no I/O). When either limit is exceeded a <c>LogWarning</c> is emitted and the method
    /// returns <see langword="false"/> — the workflow skips the always-cache merge for this
    /// run but continues normally.
    /// </remarks>
    private bool IsWithinAlwaysScopeCacheBudget(
        IReadOnlyList<ApprovalScopeRecord> scopes,
        int maxRecords,
        int maxBytes)
    {
        if (scopes.Count > maxRecords)
        {
            Workflow.Logger.LogWarning(
                "[{SessionId}] Always-scope cache load skipped: loaded {Count} records exceeds " +
                "MaxAlwaysScopeCacheRecords ({Max}). Scope-aware tools will require normal " +
                "approval this session.",
                Workflow.Info.WorkflowId, scopes.Count, maxRecords);
            return false;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(scopes, TemporalAgentJsonUtilities.DefaultOptions);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > maxBytes)
        {
            Workflow.Logger.LogWarning(
                "[{SessionId}] Always-scope cache load skipped: serialized size {Bytes:N0} bytes " +
                "exceeds MaxAlwaysScopeCacheBytes ({Max:N0}). Scope-aware tools will require " +
                "normal approval this session.",
                Workflow.Info.WorkflowId, byteCount, maxBytes);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes a session-scope <see cref="ApprovalScopeRecord"/> to the
    /// <c>temporal.approval_scopes.session</c> key in the workflow's <c>_currentStateBag</c>.
    /// Pure workflow-thread computation — no I/O, no awaits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session scope previously grew unbounded (S-1): no dedup and no cap, so a long-lived
    /// session that repeatedly re-granted the same tool/pattern would bloat the replay-carried
    /// StateBag past the 64 KB warn-only guard. This method now:
    /// </para>
    /// <list type="number">
    /// <item>Deduplicates by <c>(ToolName, Pattern)</c> — the latest <see cref="ApprovalScopeRecord.GrantedAt"/>
    /// wins, so re-granting an existing scope replaces it rather than appending.</item>
    /// <item>Bounds the record count and serialized byte size by reusing the existing always-scope
    /// budget (<c>MaxAlwaysScopeCacheRecords</c> / <c>MaxAlwaysScopeCacheBytes</c> via
    /// <see cref="IsWithinAlwaysScopeCacheBudget"/>) — no new config knobs (per plan §2.5).</item>
    /// </list>
    /// <para>
    /// <strong>Overflow behavior:</strong> when the deduplicated set would exceed the budget the
    /// new session grant is <em>rejected</em> (not written) and the approval degrades to
    /// this-call-only — the tool still executes once (the caller dispatches it regardless), but no
    /// reusable session record is persisted. Deterministic for consistent audit and replay.
    /// </para>
    /// </remarks>
    private void WriteSessionScopeToStateBag(
        string toolName,
        ApprovalScopePattern? pattern,
        string originatingRequestId)
    {
        const string sessionScopeKey = "temporal.approval_scopes.session";

        var bag = _currentStateBag is { ValueKind: not System.Text.Json.JsonValueKind.Undefined and not System.Text.Json.JsonValueKind.Null } bagEl
            ? AgentSessionStateBag.Deserialize(bagEl)
            : new AgentSessionStateBag();

        bag.TryGetValue<List<ApprovalScopeRecord>>(
            sessionScopeKey,
            out var existing,
            TemporalAgentJsonUtilities.DefaultOptions);

        var records = existing ?? new List<ApprovalScopeRecord>();

        var newRecord = new ApprovalScopeRecord
        {
            ToolName = toolName,
            Pattern = pattern,
            GrantedAt = Workflow.UtcNow,
            OriginatingRequestId = originatingRequestId,
        };

        // Dedup by (ToolName, Pattern): drop any prior record with the same identity so the
        // latest grant (this one, with the newest GrantedAt) wins. Preserves relative order of
        // surviving records, appending the new grant last.
        var newKey = SessionScopeDedupKey(toolName, pattern);
        var deduped = new List<ApprovalScopeRecord>(records.Count + 1);
        foreach (var r in records)
        {
            if (!string.Equals(SessionScopeDedupKey(r.ToolName, r.Pattern), newKey, StringComparison.Ordinal))
            {
                deduped.Add(r);
            }
        }
        deduped.Add(newRecord);

        // Bound the session cache by reusing the always-scope budget. On overflow, reject the
        // new grant (degrade to this-call-only) and keep the pre-existing records untouched.
        if (!IsWithinAlwaysScopeCacheBudget(
                deduped,
                _input!.MaxAlwaysScopeCacheRecords,
                _input!.MaxAlwaysScopeCacheBytes))
        {
            Workflow.Logger.LogWarning(
                "[{SessionId}] Session-scope grant for tool '{ToolName}' (RequestId: {RequestId}) " +
                "rejected: it would exceed the session-scope budget (reusing MaxAlwaysScopeCacheRecords/" +
                "MaxAlwaysScopeCacheBytes). Degrading this approval to this-call-only; the tool still runs " +
                "but no reusable session record is persisted.",
                Workflow.Info.WorkflowId, toolName, originatingRequestId);
            return;
        }

        bag.SetValue<List<ApprovalScopeRecord>>(
            sessionScopeKey,
            deduped,
            TemporalAgentJsonUtilities.DefaultOptions);

        _currentStateBag = bag.Serialize();

        Workflow.Logger.LogInformation(
            "[{SessionId}] Session-scope record written for tool '{ToolName}' " +
            "(RequestId: {RequestId}).",
            Workflow.Info.WorkflowId, toolName, originatingRequestId);
    }

    /// <summary>
    /// Builds a stable, deterministic dedup key for a session-scope record from its tool name
    /// and optional argument pattern. A <see langword="null"/> pattern (match-any) collapses to
    /// a distinct sentinel so it does not collide with concrete patterns.
    /// </summary>
    private static string SessionScopeDedupKey(string toolName, ApprovalScopePattern? pattern)
    {
        if (pattern is null)
        {
            return toolName + " *";
        }

        return string.Concat(
            toolName, " ",
            ((int)pattern.Type).ToString(System.Globalization.CultureInfo.InvariantCulture), " ",
            pattern.Parameter ?? "*", " ",
            pattern.Pattern);
    }

    /// <summary>
    /// Normalizes the <see cref="ApprovalScope"/> from an approved
    /// <see cref="DurableApprovalDecision"/>, applying all validation rules from spec
    /// Section 4. Returns <see cref="ApprovalScope.ThisCallOnly"/> for any invalid input.
    /// </summary>
    private ApprovalScope NormalizeApprovalScopeForPersistence(DurableApprovalDecision decision)
    {
        var (result, reason) = EvaluateScopeNormalization(decision);
        if (result == ApprovalScope.ThisCallOnly && reason is not null)
        {
            Workflow.Logger.LogWarning(
                "[{SessionId}] {Reason} Treating as ThisCallOnly.",
                Workflow.Info.WorkflowId, reason);
        }

        return result;
    }

    /// <summary>
    /// Pure, static normalization logic — extracted for unit testability.
    /// Returns the effective <see cref="ApprovalScope"/> and an optional warning reason string
    /// when the scope is degraded to <see cref="ApprovalScope.ThisCallOnly"/>.
    /// </summary>
    /// <remarks>
    /// Does not log or access workflow context. The instance method
    /// <see cref="NormalizeApprovalScopeForPersistence"/> delegates here and handles logging.
    /// </remarks>
    internal static (ApprovalScope Scope, string? DegradationReason) EvaluateScopeNormalization(
        DurableApprovalDecision decision)
    {
        var scope = decision.Scope;

        // Undefined integer value (e.g. Scope = 99)
        if (!Enum.IsDefined(typeof(ApprovalScope), scope))
        {
            return (ApprovalScope.ThisCallOnly,
                $"Undefined ApprovalScope value {(int)scope}.");
        }

        if (scope == ApprovalScope.ThisCallOnly)
            return (ApprovalScope.ThisCallOnly, null);

        // scope == Session or Always: validate the ScopePattern if non-null.
        var pattern = decision.ScopePattern;
        if (pattern is null)
        {
            // Null pattern = wildcard; valid for Session and Always.
            return (scope, null);
        }

        // Pattern string must not be null, empty, or whitespace.
        if (string.IsNullOrWhiteSpace(pattern.Pattern))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has an empty or whitespace-only Pattern.");
        }

        // Parameter must be null (wildcard) or non-whitespace.
        if (pattern.Parameter is not null && string.IsNullOrWhiteSpace(pattern.Parameter))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has a whitespace-only Parameter.");
        }

        // Defense-in-depth for non-standard deserializer paths; normal Temporal payloads reject
        // numeric PatternMatchType values at the converter boundary.
        if (!Enum.IsDefined(typeof(PatternMatchType), pattern.Type))
        {
            return (ApprovalScope.ThisCallOnly,
                $"ApprovalScope {scope} has an undefined PatternMatchType value {(int)pattern.Type}.");
        }

        // For Regex: pattern must also compile.
        if (pattern.Type == PatternMatchType.Regex)
        {
            try
            {
                _ = new Regex(pattern.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return (ApprovalScope.ThisCallOnly,
                    $"ApprovalScope {scope} has an invalid Regex pattern '{pattern.Pattern}'.");
            }
        }

        return (scope, null);
    }

    /// <summary>
    /// Replaces the always-scope cache entry in <c>_currentStateBag</c> under
    /// <c>_input!.AlwaysScopesStoreKey</c> with <paramref name="scopes"/>.
    /// Uses replacement semantics (not additive append); deduplicates by
    /// <see cref="ApprovalScopeRecord.OriginatingRequestId"/> while preserving store order.
    /// Session scopes under <c>temporal.approval_scopes.session</c> are not affected.
    /// </summary>
    private void MergeAlwaysScopesIntoStateBag(IReadOnlyList<ApprovalScopeRecord> scopes)
    {
        var storeKey = _input!.AlwaysScopesStoreKey!;

        var bag = _currentStateBag is { ValueKind: not System.Text.Json.JsonValueKind.Undefined and not System.Text.Json.JsonValueKind.Null } bagEl
            ? AgentSessionStateBag.Deserialize(bagEl)
            : new AgentSessionStateBag();

        // Deduplication by OriginatingRequestId while preserving store order.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<ApprovalScopeRecord>(scopes.Count);
        foreach (var record in scopes)
        {
            if (seen.Add(record.OriginatingRequestId))
            {
                deduped.Add(record);
            }
        }

        // Replace (not append) — replacement semantics per spec Section 7.
        bag.SetValue<List<ApprovalScopeRecord>>(
            storeKey,
            deduped,
            TemporalAgentJsonUtilities.DefaultOptions);

        _currentStateBag = bag.Serialize();
    }

    /// <summary>
    /// Deterministically merges a sequence of StateBag write-backs into <c>_currentStateBag</c>
    /// in tool-call index order (later index wins). Delegates to <see cref="StateBagMerge.Merge"/>.
    /// See that type for the full merge policy (X-1 / X-2).
    /// </summary>
    private void MergeStateBagWriteBacks(IReadOnlyList<JsonElement?> updatedBags) =>
        _currentStateBag = StateBagMerge.Merge(_currentStateBag, updatedBags);

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
        if (_input!.DurableAgentToolActivityOptions is not null
            && _input!.DurableAgentToolActivityOptions.TryGetValue(toolName, out var perTool))
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
}
