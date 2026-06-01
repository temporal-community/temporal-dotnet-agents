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

            // Fan-out: dispatch one InvokeFunctionAsync activity per tool call, in parallel.
            var toolTasks = new List<Task<DurableFunctionOutput>>(toolCalls.Count);
            foreach (var tc in toolCalls)
            {
                var toolInput = new DurableFunctionInput
                {
                    FunctionName = tc.Name,
                    Arguments = tc.Arguments is null
                        ? null
                        : new Dictionary<string, object?>(tc.Arguments),
                };

                toolTasks.Add(Workflow.ExecuteActivityAsync(
                    (DurableFunctionActivities a) => a.InvokeFunctionAsync(toolInput),
                    ResolveToolActivityOptions(tc.Name)));
            }

            // Inspect each task individually after WhenAllAsync. Never use ContinueWith inside
            // a workflow — it's non-deterministic on replay. WhenAllAsync throws if any task
            // faults; we swallow application failures here and look at each task's terminal state.
            // Workflow-level cancellation (CancellationToken fired) must propagate immediately —
            // it is distinct from a task reaching the Cancelled terminal state, which is handled
            // by the task.IsCanceled check in the per-task loop below.
            try
            {
                await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (Workflow.CancellationToken.IsCancellationRequested)
            {
                throw; // workflow cancellation — propagate, do not classify as an application error
            }
            catch
            {
                // Per-task inspection below handles both success and failure paths.
            }

            var functionResultContents = new List<AIContent>(toolCalls.Count);
            var hadError = false;
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                var task = toolTasks[i];
                if (task.IsCompletedSuccessfully)
                {
                    functionResultContents.Add(new FunctionResultContent(tc.CallId, task.Result.Result));
                }
                else if (task.IsCanceled)
                {
                    // Workflow cancellation should propagate as OperationCanceledException,
                    // not be misclassified as a consecutive application error.
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
        // already-active session.
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
        };
        return Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(carried));
    }
}
