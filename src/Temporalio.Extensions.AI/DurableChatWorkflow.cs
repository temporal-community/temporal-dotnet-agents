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
    // Per-turn metadata captured by ChatAsync before the base session loop dispatches
    // the activity. Read inside ExecuteTurnAsync to populate the activity input.
    private string? _lastClientKey;
    private string? _lastConversationId;

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
        if (input.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a chat turn: appends user messages, calls the LLM via activity,
    /// appends response, and returns the response entry.
    /// </summary>
    [WorkflowUpdate("Chat")]
    public async Task<DurableSessionResponse> ChatAsync(DurableChatInput input)
    {
        // Capture per-turn metadata for ExecuteTurnAsync. ClientKey and ConversationId
        // are carried on DurableChatInput (caller-supplied / session-client-supplied) but
        // not embedded in DurableSessionRequest, so we stash them on private fields
        // until ExecuteTurnAsync runs.
        _lastClientKey = input.ClientKey;
        _lastConversationId = input.ConversationId;

        // Build the request entry for this turn — the factory auto-generates the
        // correlation ID via Workflow.NewGuid() (deterministic, replay-safe) when the
        // caller did not supply one.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(messages, input.CorrelationId);

        var (_, responseEntry) = await RunTurnAsync(requestEntry, input.Options);
        return responseEntry;
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
        var toolOptions = Input!.ToolActivityOptions;
        if (toolOptions is null || toolOptions.Count == 0)
        {
            return ExecutePattern1TurnAsync(activityOptions, requestEntry, chatOptions);
        }

        return ExecuteDurableChatTurnAsync(activityOptions, requestEntry, chatOptions);
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
        // Flatten the entire history (including the just-appended request entry) into
        // a single message list so the LLM sees the full conversation each turn.
        var activityMessages = History
            .SelectMany(e => e.Messages)
            .ToList();

        var activityInput = new DurableChatInput
        {
            Messages = activityMessages,
            Options = chatOptions,
            ConversationId = _lastConversationId ?? Workflow.Info.WorkflowId,
            TurnNumber = CurrentTurnNumber,
            ClientKey = _lastClientKey,
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
    private async Task<ChatResponse> ExecuteDurableChatTurnAsync(
        ActivityOptions stepActivityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Seed the LLM with the flattened conversation transcript: prior turns from
        // history + the request that was just appended (which is the last entry in History
        // by the time ExecuteTurnAsync runs).
        var accumulated = History
            .SelectMany(e => e.Messages)
            .ToList();

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;
        var consecutiveErrors = 0;

        var maxIterations = Input!.MaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new DurableChatInput
            {
                Messages = accumulated,
                Options = chatOptions,
                ConversationId = _lastConversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = CurrentTurnNumber,
                ClientKey = _lastClientKey,
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
            // faults; we swallow here and look at each task's terminal state.
            try
            {
                await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);
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
                else
                {
                    hadError = true;
                    var ex = task.Exception?.InnerException ?? task.Exception;
                    var errorMessage =
                        Input!.IncludeDetailedErrors && ex is ApplicationFailureException afe
                            ? $"Error: {afe.Message} ({afe.ErrorType})"
                            : Input!.IncludeDetailedErrors && ex is not null
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
                if (consecutiveErrors > Input!.MaximumConsecutiveErrorsPerRequest)
                {
                    throw new ApplicationFailureException(
                        $"Exceeded MaximumConsecutiveErrorsPerRequest " +
                        $"({Input!.MaximumConsecutiveErrorsPerRequest}).",
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
        if (Input!.ToolActivityOptions is not null
            && Input!.ToolActivityOptions.TryGetValue(toolName, out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = Input!.ActivityTimeout,
            HeartbeatTimeout = Input!.HeartbeatTimeout,
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
