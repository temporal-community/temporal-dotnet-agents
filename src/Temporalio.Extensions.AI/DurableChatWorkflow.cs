using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal workflow that manages a durable conversation session.
/// Conversation history is persisted in workflow state as a list of
/// <see cref="DurableSessionEntry"/> instances. Chat turns are executed via
/// <c>[WorkflowUpdate]</c> for synchronous request/response semantics.
/// Includes HITL approval support via <c>[WorkflowUpdate]</c> for tool approval gates.
/// </summary>
/// <remarks>
/// Two execution modes coexist on a single turn:
/// <list type="bullet">
///   <item>
///     <b>Pattern 1</b> (inline tools): when
///     <see cref="DurableChatWorkflowInput.ToolActivityOptions"/> is null/empty, one
///     <c>GetResponseAsync</c> activity handles the LLM call and any inline tool
///     invocation (via <c>FunctionInvokingChatClient</c> in the chain).
///   </item>
///   <item>
///     <b>Pattern 3</b> (durable tool dispatch): when <c>ToolActivityOptions</c> is
///     populated, the workflow drives a fan-out loop —
///     <c>GetChatStepAsync</c> for the LLM call, then one
///     <c>InvokeFunctionAsync</c> activity per tool call, then back to the LLM
///     with the synthesized tool-result message — until the model returns a final
///     assistant message or <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/>
///     is exceeded.
///   </item>
/// </list>
/// </remarks>
[Workflow("Temporalio.Extensions.AI.DurableChatWorkflow")]
internal sealed class DurableChatWorkflow : DurableChatWorkflowBase<ChatResponse>
{
    // Per-turn metadata keyed by DurableSessionRequest object reference.
    // Using ReferenceEqualityComparer because DurableSessionRequest.FromMessages always
    // returns a new object, so each turn has a distinct key even if CorrelationId is reused.
    // The entry is removed in the finally block of ChatAsync so cancelled/failed turns do
    // not leak entries for the workflow's lifetime.
    private readonly Dictionary<DurableSessionRequest, (string? ClientKey, string? ConversationId)>
        _perTurnMeta = new(ReferenceEqualityComparer.Instance);

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    /// <summary>
    /// Validates a chat request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ChatAsync))]
    public void ValidateChat(DurableChatInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input.Messages is null or { Count: 0 })
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a chat turn: appends user messages, calls the LLM via activity,
    /// appends response, and returns the response entry.
    /// </summary>
    [WorkflowUpdate("Chat")]
    public async Task<DurableSessionResponse> ChatAsync(DurableChatInput input)
    {
        // Build the request entry for this turn — the factory auto-generates the
        // correlation ID via Workflow.NewGuid() (deterministic, replay-safe) when the
        // caller did not supply one.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(messages, input.CorrelationId);

        // Store per-turn metadata keyed by the request entry object reference.
        // Removed in the finally block so exceptions (cancellation, timeout, tool failure)
        // do not leak the entry for the workflow's lifetime.
        _perTurnMeta[requestEntry] = (input.ClientKey, input.ConversationId);
        try
        {
            var (_, responseEntry) = await RunTurnAsync(requestEntry, input.Options);
            return responseEntry;
        }
        finally
        {
            _perTurnMeta.Remove(requestEntry);
        }
    }

    /// <summary>
    /// Wraps the activity's <see cref="ChatResponse"/> into a <see cref="DurableSessionResponse"/>
    /// for history storage.
    /// </summary>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ChatResponse output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output, createdAt);

    protected override Task<ChatResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Pattern 3 activates when the session client populated per-tool ActivityOptions
        // at workflow start. The decision is frozen in workflow history, so replay is
        // deterministic regardless of which worker picks up the activation.
        var toolOptions = RequiredInput.ToolActivityOptions;
        if (toolOptions is null || toolOptions.Count == 0)
        {
            return ExecutePattern1TurnAsync(activityOptions, requestEntry, chatOptions);
        }

        return ExecutePattern3TurnAsync(activityOptions, requestEntry, chatOptions);
    }

    /// <summary>
    /// Pattern 1 single-activity dispatch. Preserved verbatim from the original
    /// implementation so existing workflows keep their event history shape.
    /// </summary>
    private Task<ChatResponse> ExecutePattern1TurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        if (!_perTurnMeta.TryGetValue(requestEntry, out var meta))
            throw new InvalidOperationException(
                $"Per-turn metadata missing for request {requestEntry.CorrelationId}. This is a bug.");

        // Flatten the entire history (including the just-appended request entry) into
        // a single message list so the LLM sees the full conversation each turn.
        var activityMessages = History
            .SelectMany(e => e.Messages)
            .ToList();

        var activityInput = new DurableChatInput
        {
            Messages = activityMessages,
            Options = chatOptions,
            ConversationId = meta.ConversationId ?? Workflow.Info.WorkflowId,
            TurnNumber = CurrentTurnNumber,
            ClientKey = meta.ClientKey,
            CorrelationId = requestEntry.CorrelationId,
        };
        return Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(activityInput),
            activityOptions);
    }

    /// <summary>
    /// Pattern 3 dispatch loop. Alternates between <c>GetChatStepAsync</c> (one LLM call,
    /// returns raw <see cref="FunctionCallContent"/>) and one <c>InvokeFunctionAsync</c>
    /// activity per tool call (fanned out in parallel via
    /// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>). Loop exits
    /// when the LLM returns a final assistant message or
    /// <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/> is exceeded — the latter
    /// synthesizes a sentinel <see cref="ChatResponse"/> rather than throwing, matching the
    /// behavior of MAF's <c>AgentWorkflow</c>.
    /// </summary>
    private async Task<ChatResponse> ExecutePattern3TurnAsync(
        ActivityOptions stepActivityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        if (!_perTurnMeta.TryGetValue(requestEntry, out var meta))
            throw new InvalidOperationException(
                $"Per-turn metadata missing for request {requestEntry.CorrelationId}. This is a bug.");

        // Seed the LLM with the flattened conversation transcript: prior turns from
        // history + the request that was just appended (which is the last entry in History
        // by the time ExecuteTurnAsync runs).
        var accumulated = History
            .SelectMany(e => e.Messages)
            .ToList();

        List<ChatMessage> allTurnMessages = [];
        UsageDetails? totalUsage = null;
        var consecutiveErrors = 0;

        var maxIterations = RequiredInput.MaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new DurableChatInput
            {
                Messages = accumulated,
                Options = chatOptions,
                ConversationId = meta.ConversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = CurrentTurnNumber,
                ClientKey = meta.ClientKey,
                CorrelationId = requestEntry.CorrelationId,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (DurableChatActivities a) => a.GetChatStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount =
                    (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount =
                    (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount =
                    (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.Add(stepResult.AssistantMessage);
            allTurnMessages.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                return new ChatResponse(allTurnMessages) { Usage = totalUsage };
            }

            var toolCalls = stepResult.ToolCalls;

            // ── Phase 1: Fan out interceptor activities in parallel ────────────────────────
            // Build interceptor results for all tool calls. Tools opted out (SkipInterceptor)
            // or when no interceptor is configured get a synthetic Proceed.
            DurableToolInterceptorResult[]? interceptorResults = null;
            var interceptorActivityOpts = RequiredInput.InterceptorActivityOptions;
            var interceptorToolOpts = RequiredInput.InterceptorToolActivityOptions;
            var skippedTools = RequiredInput.InterceptorSkippedTools;

            if (interceptorActivityOpts is not null)
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
                            ToolName = tc.Name,
                            Arguments = tc.Arguments is null
                                ? null
                                : new Dictionary<string, object?>(tc.Arguments),
                            CallId = tc.CallId,
                            ConversationId = meta.ConversationId,
                            CorrelationId = requestEntry.CorrelationId,
                            TurnNumber = CurrentTurnNumber,
                        };

                        // See also: AgentWorkflow.ExecuteDurableAgentTurnAsync (MAF path) — parallel typed dispatch
                        interceptorTasks.Add(Workflow.ExecuteActivityAsync(
                            (DurableChatActivities a) => a.RunToolInterceptorAsync(interceptorInput),
                            DurableToolDecisionPolicy.ResolveInterceptorActivityOptions(tc.Name, interceptorActivityOpts, interceptorToolOpts)));
                    }
                }

                interceptorResults = await Workflow.WhenAllAsync(interceptorTasks).ConfigureAwait(true);
            }

            // ── Phase 2: Process decisions, park for approvals ────────────────────────────
            //
            // Safety invariant (BLOCK-4): NO tool activity is dispatched until ALL approval
            // waits in this turn are fully resolved. Proceed-outcome tool inputs are buffered
            // in pendingToolDispatches and dispatched in Phase 2.5, after the approval loop
            // completes. This prevents a write-style tool (e.g. send_email, apply_refund) from
            // executing concurrently with a human review window opened by another tool in the
            // same turn.
            //
            var toolTasks = new Task<DurableFunctionOutput>?[toolCalls.Count];
            var syntheticResults = new string?[toolCalls.Count]; // null = real tool result

            // Buffered dispatches: populated during Phase 2, dispatched in Phase 2.5.
            var pendingToolDispatches = new List<(int Index, DurableFunctionInput Input, ActivityOptions Options)>(toolCalls.Count);

            var requiresApprovalTools = RequiredInput.RequiresApprovalTools;

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
                    {
                        // Buffer for dispatch after all approval waits resolve (BLOCK-4).
                        pendingToolDispatches.Add((i, new DurableFunctionInput
                        {
                            FunctionName = tc.Name,
                            Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                        }, ResolveToolActivityOptions(tc.Name)));
                        break;
                    }

                    case DurableToolOutcome.PauseForApproval:
                    {
                        // Park the turn loop; wait for a human decision via DurableApprovalMixin
                        // (compute-free durable wait).
                        var approvalRequest = new DurableApprovalRequest
                        {
                            RequestId = $"{tc.CallId ?? tc.Name}-{Workflow.NewGuid():N}",
                            FunctionName = tc.Name,
                            CallId = tc.CallId,
                            Description = DurableToolDecisionPolicy.GetApprovalDescription(interceptorResult, tc.Name),
                        };

                        // Sequential: the mixin enforces one pending approval at a time.
                        var decision = await RequestApprovalFromTurnLoopAsync(
                            approvalRequest,
                            RequiredInput.ApprovalTimeout,
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
                            pendingToolDispatches.Add((i, new DurableFunctionInput
                            {
                                FunctionName = tc.Name,
                                Arguments = DurableToolDecisionPolicy.GetEffectiveArguments(interceptorResult?.ModifiedArguments, (IReadOnlyDictionary<string, object?>?)tc.Arguments),
                            }, ResolveToolActivityOptions(tc.Name)));
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
                    }

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
            // All approval waits are resolved before any InvokeFunction activity starts.
            foreach (var (idx, funcInput, opts) in pendingToolDispatches)
            {
                toolTasks[idx] = Workflow.ExecuteActivityAsync(
                    (DurableFunctionActivities a) => a.InvokeFunctionAsync(funcInput),
                    opts);
            }

            // ── Phase 3: Wait for all dispatched tool activities ──────────────────────────
            // Inspect each task individually after WhenAllAsync. Never use ContinueWith inside
            // a workflow — it's non-deterministic on replay. WhenAllAsync throws if any task
            // faults; we swallow application failures here and look at each task's terminal state.
            // Workflow-level cancellation must propagate immediately. Two paths arrive here:
            // (a) WhenAllAsync itself throws OperationCanceledException when the workflow token fires.
            // (b) Temporal delivers a cancelled activity as a Faulted task (ActivityFailureException
            //     wrapping a cancellation cause), not as a Cancelled task — caught by the bare catch
            //     below and re-detected via Workflow.CancellationToken.IsCancellationRequested.
            var pendingRealTasks = toolTasks
                .Where(t => t is not null)
                .Cast<Task<DurableFunctionOutput>>()
                .ToList();

            if (pendingRealTasks.Count > 0)
            {
                try
                {
                    await Workflow.WhenAllAsync(pendingRealTasks).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (Workflow.CancellationToken.IsCancellationRequested)
                {
                    throw; // workflow cancellation as OCE — do not classify as an application error
                }
                catch
                {
                    // If the workflow is being cancelled, faulted activity tasks may represent
                    // Temporal-delivered cancellations rather than application errors.
                    if (Workflow.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(
                            "Workflow cancelled during tool fan-out; propagating cancellation.");
                    }
                    // Per-task inspection below handles application-level failures.
                }
            }

            // Assemble final results in original order (synthetic and real interleaved by index).
            var functionResultContents = new List<AIContent>(toolCalls.Count);
            var hadError = false;
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];

                if (syntheticResults[i] is { } synthetic)
                {
                    // Skip or Block outcome — inject synthetic result directly.
                    functionResultContents.Add(new FunctionResultContent(tc.CallId, synthetic));
                    continue;
                }

                var task = toolTasks[i];
                if (task is null)
                {
                    // Tool was denied (PauseForApproval + denial): syntheticResults[i] was set,
                    // so we should have hit the branch above. This is a defensive fallback.
                    functionResultContents.Add(new FunctionResultContent(tc.CallId, "[Denied] Tool call was denied."));
                    continue;
                }

                if (task.IsCompletedSuccessfully)
                {
                    functionResultContents.Add(new FunctionResultContent(tc.CallId, task.Result.Result));
                }
                else if (task.IsCanceled || Workflow.CancellationToken.IsCancellationRequested)
                {
                    // Workflow cancellation should propagate as OperationCanceledException.
                    // Temporal may deliver cancelled activities as Faulted (not Cancelled) tasks,
                    // so IsCanceled alone is insufficient — check the workflow token too.
                    throw new OperationCanceledException(
                        "A tool activity was cancelled, propagating cancellation.");
                }
                else
                {
                    hadError = true;
                    var ex = task.Exception?.InnerException ?? task.Exception;
                    var includeDetails = RequiredInput.IncludeDetailedErrors;
                    var errorMessage =
                        includeDetails && ex is ApplicationFailureException afe
                            ? $"Error: {afe.Message} ({afe.ErrorType})"
                            : includeDetails && ex is not null
                                ? $"Error: {ex.GetType().Name}: {ex.Message}"
                                : "Error: Tool invocation failed.";

                    // CRITICAL: synthesize a FunctionResultContent for EVERY CallId in original
                    // order — OpenAI/Anthropic reject tool turns with missing call IDs.
                    functionResultContents.Add(new FunctionResultContent(tc.CallId, errorMessage));
                }
            }

            if (hadError)
            {
                consecutiveErrors++;
                if (consecutiveErrors > RequiredInput.MaximumConsecutiveErrorsPerRequest)
                {
                    throw new ApplicationFailureException(
                        $"Exceeded MaximumConsecutiveErrorsPerRequest ({RequiredInput.MaximumConsecutiveErrorsPerRequest}).",
                        nonRetryable: true);
                }
            }
            else
            {
                consecutiveErrors = 0;
            }

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
        }

        // Iteration cap hit. Per OD-9 we synthesize an explicit sentinel message rather than
        // throwing — workflow continues, history stays consistent, caller gets a clear signal.
        Workflow.Logger.LogWarning(
            "Pattern 3 turn aborted after {Max} tool-call iterations; LLM did not converge.",
            maxIterations);

        var sentinel = new ChatMessage(
            ChatRole.Assistant,
            $"Maximum tool-call iterations ({maxIterations}) exceeded; " +
            "the conversation did not converge on a final answer.");
        allTurnMessages.Add(sentinel);

        return new ChatResponse(allTurnMessages) { Usage = totalUsage };
    }

    /// <summary>
    /// Resolves per-tool <see cref="ActivityOptions"/> for a tool dispatch. Falls back to
    /// the workflow-level defaults when no entry exists for the tool name (defensive — the
    /// session client eagerly populates every registered tool).
    /// </summary>
    private ActivityOptions ResolveToolActivityOptions(string toolName)
    {
        if (RequiredInput.ToolActivityOptions is not null
            && RequiredInput.ToolActivityOptions.TryGetValue(toolName, out var perTool))
        {
            return perTool;
        }

        Workflow.Logger.LogWarning(
            "Tool '{ToolName}' not found in ToolActivityOptions; using defaults. Check session client registration.",
            toolName);

        return new ActivityOptions
        {
            StartToCloseTimeout = RequiredInput.ActivityTimeout,
            HeartbeatTimeout = RequiredInput.HeartbeatTimeout,
            Summary = toolName,
        };
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input)
    {
        // Carry the Pattern 3 activation marker AND the iteration cap forward so the next
        // run preserves the activation decision and configured loop behavior. Per-tool
        // option freezes ensure mid-CAN drift cannot change the activation state of an
        // already-active session. Interceptor config is also carried forward verbatim.
        var carried = new DurableChatWorkflowInput
        {
            TimeToLive = input.TimeToLive,
            CarriedHistory = input.CarriedHistory,
            ActivityTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducer = input.HistoryReducer,
            OriginalCreatedAt = input.OriginalCreatedAt,
            ToolActivityOptions = Input?.ToolActivityOptions ?? input.ToolActivityOptions,
            MaxToolCallsPerTurn = Input?.MaxToolCallsPerTurn ?? input.MaxToolCallsPerTurn,
            MaximumConsecutiveErrorsPerRequest =
                Input?.MaximumConsecutiveErrorsPerRequest ?? input.MaximumConsecutiveErrorsPerRequest,
            IncludeDetailedErrors = Input?.IncludeDetailedErrors ?? input.IncludeDetailedErrors,
            InterceptorActivityOptions = Input?.InterceptorActivityOptions ?? input.InterceptorActivityOptions,
            InterceptorToolActivityOptions = Input?.InterceptorToolActivityOptions ?? input.InterceptorToolActivityOptions,
            InterceptorSkippedTools = Input?.InterceptorSkippedTools ?? input.InterceptorSkippedTools,
            RequiresApprovalTools = Input?.RequiresApprovalTools ?? input.RequiresApprovalTools,
        };
        return Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(carried));
    }
}
