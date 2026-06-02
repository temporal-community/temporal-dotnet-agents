using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
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
        // activityOptions from the base is intentionally not used — AgentWorkflow constructs its own
        // ActivityOptions from _input.RetryPolicy to apply MAF-specific retry policy and summary.
        // If the base class gains new required fields in activityOptions, revisit this override.
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
            AgentToolInterceptorResult[]? interceptorResults = null;
            var interceptorOpts = _input!.InterceptorActivityOptions;
            var skippedTools = _input!.InterceptorSkippedTools;

            if (interceptorOpts is not null)
            {
                var interceptorTasks = new List<Task<AgentToolInterceptorResult>>(toolCalls.Count);
                foreach (var tc in toolCalls)
                {
                    var isSkipped = skippedTools is not null
                        && skippedTools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase);

                    if (isSkipped)
                    {
                        interceptorTasks.Add(Task.FromResult(
                            new AgentToolInterceptorResult { Outcome = AgentToolOutcome.Proceed }));
                    }
                    else
                    {
                        var interceptorInput = new AgentToolInterceptorInput
                        {
                            AgentName = _input!.AgentName,
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null
                                ? null
                                : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            SerializedStateBag = _currentStateBag,
                        };

                        // Per-tool interceptor timeout if configured. Set summary per-tool.
                        var perToolInterceptorOpts = new ActivityOptions
                        {
                            StartToCloseTimeout = interceptorOpts.StartToCloseTimeout,
                            HeartbeatTimeout = interceptorOpts.HeartbeatTimeout,
                            RetryPolicy = interceptorOpts.RetryPolicy,
                            Summary = $"intercept:{tc.Name}",
                        };

                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (AgentActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            perToolInterceptorOpts));
                    }
                }

                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);
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

                // Determine effective outcome.
                var outcome = interceptorResult?.Outcome ?? AgentToolOutcome.Proceed;

                // Rule 2: RequireApproval is an absolute floor. Block is strictly stricter than
                // approval and is honoured as-is; every other outcome (Proceed, Skip,
                // PauseForApproval) is overridden to PauseForApproval so the approval gate
                // cannot be bypassed by an interceptor returning Skip (BLOCK-3 fix).
                var toolRequiresApproval = requiresApprovalTools is not null
                    && requiresApprovalTools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase);

                if (toolRequiresApproval && outcome != AgentToolOutcome.Block)
                {
                    outcome = AgentToolOutcome.PauseForApproval;
                }

                switch (outcome)
                {
                    case AgentToolOutcome.Proceed:
                        // Buffer for dispatch after all approval waits resolve (BLOCK-4).
                        var effectiveArgs = interceptorResult?.ModifiedArguments is { } modArgs
                            ? modArgs
                            : (tc.Arguments is null ? null : new Dictionary<string, object?>(tc.Arguments));
                        pendingToolDispatches.Add((i, new InvokeAgentToolInput
                        {
                            AgentName = _input!.AgentName,
                            ToolName = tc.Name,
                            Arguments = effectiveArgs,
                            CallId = tc.CallId,
                        }, ResolveDurableToolActivityOptions(tc.Name)));
                        break;

                    case AgentToolOutcome.PauseForApproval:
                        // Feature A: park the turn loop; wait for a human decision via
                        // the DurableApprovalMixin (compute-free durable wait).
                        var approvalDescription = interceptorResult?.EnrichedDescription
                            ?? interceptorResult?.Message
                            ?? $"Approve invocation of tool '{tc.Name}'";

                        var approvalRequest = new DurableApprovalRequest
                        {
                            RequestId = $"{tc.CallId ?? tc.Name}-{Workflow.NewGuid():N}",
                            FunctionName = tc.Name,
                            CallId = tc.CallId,
                            Description = approvalDescription,
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
                            // Buffer the approved tool for dispatch in Phase 2.5.
                            var approvedArgs = interceptorResult?.ModifiedArguments is { } mArgs
                                ? mArgs
                                : (tc.Arguments is null ? null : new Dictionary<string, object?>(tc.Arguments));
                            pendingToolDispatches.Add((i, new InvokeAgentToolInput
                            {
                                AgentName = _input!.AgentName,
                                ToolName = tc.Name,
                                Arguments = approvedArgs,
                                CallId = tc.CallId,
                            }, ResolveDurableToolActivityOptions(tc.Name)));
                        }
                        else
                        {
                            // Denied or timed out — inject an error result.
                            var denialReason = string.IsNullOrEmpty(decision.Reason)
                                ? "Tool call was denied or timed out."
                                : decision.Reason;
                            syntheticResults[i] = $"[Denied] {denialReason}";
                        }
                        break;

                    case AgentToolOutcome.Skip:
                        syntheticResults[i] = interceptorResult?.Message ?? string.Empty;
                        break;

                    case AgentToolOutcome.Block:
                    default:
                        syntheticResults[i] = $"[Blocked] {interceptorResult?.Message ?? "Tool execution was blocked."}";
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
            var functionResultContents = new List<AIContent>(toolCalls.Count);
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
                    functionResultContents.Add(new FunctionResultContent(
                        callId: toolCalls[i].CallId,
                        result: toolResults[pendingIdx++].Result));
                }
            }

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
