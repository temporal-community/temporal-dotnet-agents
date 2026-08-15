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
/// produced or <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/> is exceeded.
/// </remarks>
[Workflow("TemporalCommunity.Extensions.AI.DurableChatWorkflow")]
internal sealed class DurableChatWorkflow : DurableChatWorkflowBase<ChatResponse>
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
        if (input.ToolsetManifest is not null && input.ToolDeclarations is not null)
        {
            throw new ApplicationFailureException(
                "A durable chat session cannot combine caller-owned declarations with a " +
                "worker-owned toolset manifest.",
                errorType: nameof(Exceptions.DurableConfigurationException),
                nonRetryable: true);
        }

        if (input.ToolsetManifest is null && input.ToolDeclarations is null)
        {
            var resolverOptions = new ActivityOptions
            {
                StartToCloseTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                RetryPolicy = Internal.DefaultRetryPolicy.Resolve(input.RetryPolicy),
                Summary = "Resolve durable toolsets",
            };
            var manifest = await Workflow.ExecuteActivityAsync(
                (DurableToolsetActivities activities) => activities.ResolveDurableToolsetsAsync(
                    new Internal.DurableToolsetResolutionRequest { UseWorkerDefaults = true }),
                resolverOptions).ConfigureAwait(true);
            manifest.Validate();
            input = input with { ToolsetManifest = manifest };
        }
        else
        {
            input.ToolsetManifest?.Validate();
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

    /// <inheritdoc/>
    protected override Task<List<Session.DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<Session.DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.ReduceHistoryByKeyAsync(
                new ReduceHistoryByKeyInput { ReducerKey = reducerKey, History = history }),
            activityOptions);

    protected override async Task<ChatResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        if (!_perTurnMeta.TryGetValue(requestEntry, out var metadata))
        {
            throw new InvalidOperationException(
                $"Per-turn metadata missing for request {requestEntry.CorrelationId}. This is a bug.");
        }

        var result = await ExecuteManagedToolLoopTurnAsync(
            activityOptions,
            requestEntry,
            chatOptions,
            metadata.ClientKey,
            metadata.ConversationId,
            dispatchMode: DurableToolDispatchMode.Parallel).ConfigureAwait(true);
        return result.Response;
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(input));
}
