using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

public abstract partial class DurableChatWorkflowBase<TOutput>
{
    /// <summary>
    /// Durable tool-dispatch loop. Alternates between
    /// <c>GetChatStepAsync</c> (one LLM call, returns raw <see cref="FunctionCallContent"/>)
    /// and one <c>InvokeFunctionAsync</c> activity per tool call (fanned out in parallel via
    /// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>). Loop exits
    /// when the LLM returns a final assistant message or
    /// <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/> is exceeded — the latter
    /// synthesizes a sentinel <see cref="ChatResponse"/> rather than throwing, matching the
    /// behavior of MAF's <c>AgentWorkflow</c>.
    /// </summary>
    internal async Task<DurableManagedLoopResult> ExecuteManagedToolLoopTurnAsync(
        ActivityOptions stepActivityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions,
        string? clientKey,
        string? conversationId)
    {
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
                ConversationId = conversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = CurrentTurnNumber,
                ClientKey = clientKey,
                CorrelationId = requestEntry.CorrelationId,
            };

            DurableChatStepResult stepResult;
            try
            {
                stepResult = await Workflow.ExecuteActivityAsync(
                    (DurableChatActivities a) => a.GetChatStepAsync(stepInput),
                    stepActivityOptions).ConfigureAwait(true);
            }
            catch (ActivityFailureException) when (Workflow.CancellationToken.IsCancellationRequested)
            {
                // Workflow cancellation delivered as a faulted activity — propagate, never count.
                throw;
            }
            catch (ActivityFailureException llmFailure)
            {
                // Retry-hardening (Part 3): the LLM-step activity exhausted its RetryPolicy (bounded
                // backstop) or failed fast (non-retryable HTTP 4xx classified in the activity). This
                // arrives as an ActivityFailureException. Route it through the SAME consecutive-error
                // counter that tool failures use so the MaximumConsecutiveErrorsPerRequest bound
                // terminates the turn instead of the loop swallowing the failure and re-dispatching
                // the same doomed call forever. At threshold we surface a terminal non-retryable
                // failure so SendAsync returns rather than hanging.
                consecutiveErrors++;
                if (consecutiveErrors > RequiredInput.MaximumConsecutiveErrorsPerRequest)
                {
                    throw new ApplicationFailureException(
                        $"LLM step failed and exceeded MaximumConsecutiveErrorsPerRequest " +
                        $"({RequiredInput.MaximumConsecutiveErrorsPerRequest}).",
                        llmFailure,
                        errorType: "LlmStepConsecutiveErrors",
                        nonRetryable: true);
                }

                // Below threshold — retry the LLM step on the next iteration. No assistant message
                // was produced, so nothing is appended to the accumulated transcript this iteration.
                continue;
            }

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
                return new DurableManagedLoopResult(
                    new ChatResponse(allTurnMessages) { Usage = totalUsage },
                    DurableTurnCompletionReason.FinalResponse);
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
                            ConversationId = conversationId,
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
                            // Metadata is deliberately interceptor-authored. Do not expose raw
                            // model function arguments to a reviewer unless an interceptor has
                            // first reduced them to explicit, safe review data.
                            ReviewData = interceptorResult?.Metadata,
                        };

                        // Sequential: the mixin enforces one pending approval at a time.
                        var decision = await RequestApprovalFromTurnLoopAsync(
                            approvalRequest,
                            ResolveApprovalTimeout(tc.Name),
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
                    // S-X-6: task.Result.Result crosses the activity boundary as a JsonElement
                    // (declared object?), so FunctionResultContent.Result holds a JsonElement here,
                    // not the tool's domain type. Accepted limitation — see DurableFunctionOutput.Result.
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
            "Durable tool-loop turn aborted after {Max} tool-call iterations; LLM did not converge.",
            maxIterations);

        var sentinel = new ChatMessage(
            ChatRole.Assistant,
            $"Maximum tool-call iterations ({maxIterations}) exceeded; " +
            "the conversation did not converge on a final answer.");
        allTurnMessages.Add(sentinel);

        return new DurableManagedLoopResult(
            new ChatResponse(allTurnMessages) { Usage = totalUsage },
            DurableTurnCompletionReason.IterationLimitReached);
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
            // Apply the configured retry policy so an unmapped tool does not fall back to
            // Temporal's default (unlimited retries) — a non-idempotent unregistered tool
            // would otherwise retry forever. Bounded default when unset. The MAF path already does this.
            RetryPolicy = Internal.DefaultRetryPolicy.Resolve(RequiredInput.RetryPolicy),
            Summary = toolName,
        };
    }

    /// <summary>
    /// Resolves the reviewer deadline for a tool approval. Per-tool values are frozen in the
    /// workflow input at session start; the session-wide timeout is the fallback.
    /// </summary>
    private TimeSpan ResolveApprovalTimeout(string toolName)
    {
        if (RequiredInput.ToolApprovalTimeouts is not null
            && RequiredInput.ToolApprovalTimeouts.TryGetValue(toolName, out var timeout))
        {
            return timeout;
        }

        return RequiredInput.ApprovalTimeout;
    }


}

internal sealed record DurableManagedLoopResult(
    ChatResponse Response,
    DurableTurnCompletionReason CompletionReason);
