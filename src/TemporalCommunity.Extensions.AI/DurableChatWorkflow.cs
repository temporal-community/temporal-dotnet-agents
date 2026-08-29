using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tools;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Temporal workflow that manages a durable conversation session.
/// Conversation history is persisted in workflow state as a list of
/// <see cref="DurableSessionEntry"/> instances. Chat turns are executed via
/// <c>[WorkflowUpdate]</c> for synchronous request/response semantics.
/// Includes HITL approval support via <c>[WorkflowUpdate]</c> for tool approval gates.
/// </summary>
/// <remarks>
/// Each turn uses the workflow-owned model/tool loop: <c>GetChatStepAsync</c> performs one
/// model call, every returned tool request is dispatched as an <c>InvokeFunctionAsync</c>
/// activity, and results are fed to the next model call until a final assistant response is
/// produced, the provider reports an incomplete response, or
/// <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/> is exceeded. Only calls authorized
/// by the provider finish reason (or a legacy null reason) are dispatched.
/// </remarks>
[Workflow("TemporalCommunity.Extensions.AI.DurableChatWorkflow")]
internal sealed class DurableChatWorkflow : DurableChatWorkflowBase<DurableManagedLoopResult>
{
    // Per-turn metadata keyed by DurableSessionRequest object reference.
    // Using ReferenceEqualityComparer because DurableSessionRequest.FromMessages always
    // returns a new object, so each turn has a distinct key even if CorrelationId is reused.
    // The entry is removed in the finally block of ChatAsync so cancelled/failed turns do
    // not leak entries for the workflow's lifetime.
    private readonly Dictionary<DurableSessionRequest, (string? ClientKey, string? ConversationId)>
        _perTurnMeta = new(Internal.ReferenceComparer<DurableSessionRequest>.Instance);
    private bool _toolAuthorityReady;

    [WorkflowRun]
    public new async Task RunAsync(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InitializeInput(input);
        var authority = Internal.DurableToolsetAuthority.Resolve(input);
        // Legacy caller-owned histories serialized an empty ToolActivityOptions dictionary.
        // Current worker-owned starts omit that field and resolve their default toolsets once.
        // This wire-shape distinction preserves both command sequences without consulting live
        // worker state during replay.
        if (authority == Internal.DurableToolsetAuthorityKind.None
            && input.ToolActivityOptions is null)
        {
            var resolverOptions = new ActivityOptions
            {
                StartToCloseTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                RetryPolicy = Internal.DefaultRetryPolicy.ResolveForTool(input.RetryPolicy),
                Summary = "Resolve durable toolsets",
            };
            var manifest = await Workflow.ExecuteActivityAsync(
                (DurableToolsetActivities activities) => activities.ResolveDurableToolsetsAsync(
                    new Internal.DurableToolsetResolutionRequest { UseWorkerDefaults = true }),
                resolverOptions).ConfigureAwait(true);
            manifest.Validate();
            input = input with { ToolsetManifest = manifest };
        }
        var sessionTask = base.RunAsync(input);
        _toolAuthorityReady = true;
        await sessionTask.ConfigureAwait(true);
    }

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
        await Workflow.WaitConditionAsync(() => _toolAuthorityReady).ConfigureAwait(true);

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
            var (output, responseEntry) = await RunTurnAsync(requestEntry, input.Options);

            // The history entry may contain a protocol-safe sentinel. The caller receives the
            // original diagnostic response and its terminal metadata.
            return DurableSessionResponse.FromChatResponse(
                requestEntry.CorrelationId,
                output.Response,
                responseEntry.CreatedAt,
                output.CompletionReason);
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
        DurableManagedLoopResult output,
        DateTimeOffset createdAt)
    {
        var historyResponse = output.CompletionReason ==
            DurableTurnCompletionReason.IncompleteResponse
                ? DurableManagedLoopHistory.ForIncompleteResponse(output.Response)
                : output.Response;

        return DurableSessionResponse.FromChatResponse(
            correlationId,
            historyResponse,
            createdAt,
            output.CompletionReason);
    }

    /// <inheritdoc/>
    protected override Task<List<Session.DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<Session.DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.ReduceHistoryByKeyAsync(
                new ReduceHistoryByKeyInput { ReducerKey = reducerKey, History = history }),
            activityOptions);

    protected override async Task<DurableManagedLoopResult> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        if (!_perTurnMeta.TryGetValue(requestEntry, out var metadata))
        {
            throw new InvalidOperationException(
                $"Per-turn metadata missing for request {requestEntry.CorrelationId}. This is a bug.");
        }

        return await ExecuteManagedToolLoopTurnAsync(
            activityOptions,
            requestEntry,
            chatOptions,
            metadata.ClientKey,
            metadata.ConversationId,
            dispatchMode: DurableToolDispatchMode.Parallel).ConfigureAwait(true);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(input));
}
