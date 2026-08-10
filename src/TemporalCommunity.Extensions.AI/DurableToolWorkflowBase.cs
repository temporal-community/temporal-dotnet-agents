using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Workflow base for application-owned Updates that use the package-managed model/tool loop with
/// one Temporal activity per model step and per real tool invocation.
/// </summary>
/// <typeparam name="TRequestData">Immutable application data for one turn.</typeparam>
/// <typeparam name="TTurnState">Application-owned working state and structured output for one turn.</typeparam>
public abstract class DurableToolWorkflowBase<TRequestData, TTurnState>
    : DurableChatWorkflowBase<DurableTurnResult<TTurnState>>
{
    private readonly Dictionary<DurableSessionRequest, DurableTurnRequest<TRequestData, TTurnState>>
        _turnRequests = new(Internal.ReferenceComparer<DurableSessionRequest>.Instance);
    private readonly HashSet<string> _managedUpdateIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs one package-managed durable turn. This method must be called from a workflow Update,
    /// and may be called at most once for that Update ID in the current workflow run.
    /// </summary>
    protected async Task<DurableTurnResult<TTurnState>> RunDurableTurnAsync(
        DurableTurnRequest<TRequestData, TTurnState> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Workflow.CurrentUpdateInfo is not { } updateInfo)
        {
            throw new InvalidOperationException(
                $"{nameof(RunDurableTurnAsync)} must be called from a workflow Update handler.");
        }

        if (!_managedUpdateIds.Add(updateInfo.Id))
        {
            throw new ApplicationFailureException(
                $"Workflow Update '{updateInfo.Id}' already started a durable turn in this run.",
                errorType: "DurableTurnAlreadyStarted",
                nonRetryable: true);
        }

        if (request.Messages is null or { Count: 0 })
        {
            throw new ArgumentException("At least one message is required.", nameof(request));
        }

        if (request.Options is null)
        {
            throw new ArgumentException("Turn options cannot be null.", nameof(request));
        }

        if (!Enum.IsDefined(typeof(DurableToolDispatchMode), request.Options.DispatchMode))
        {
            throw new ApplicationFailureException(
                $"Unknown durable tool dispatch mode '{(int)request.Options.DispatchMode}'.",
                errorType: "DurableTurnInvalidDispatchMode",
                nonRetryable: true);
        }

        var requestEntry = DurableSessionRequest.FromMessages(
            request.Messages,
            request.CorrelationId);
        _turnRequests.Add(requestEntry, request);

        try
        {
            var (result, _) = await RunTurnAsync(
                requestEntry,
                request.ChatOptions,
                cancellationToken).ConfigureAwait(true);
            return result;
        }
        finally
        {
            _turnRequests.Remove(requestEntry);
        }
    }

    /// <inheritdoc/>
    protected sealed override async Task<DurableTurnResult<TTurnState>> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        if (!_turnRequests.TryGetValue(requestEntry, out var request))
        {
            throw new InvalidOperationException(
                $"Typed turn data is missing for request '{requestEntry.CorrelationId}'.");
        }

        var loopResult = await ExecuteManagedToolLoopTurnAsync(
            activityOptions,
            requestEntry,
            chatOptions,
            clientKey: null,
            request.ConversationId,
            JsonSerializer.SerializeToElement(
                request.RequestData,
                DurableAIJsonUtilities.DefaultOptions),
            JsonSerializer.SerializeToElement(
                request.InitialTurnState,
                DurableAIJsonUtilities.DefaultOptions),
            request.Options.DispatchMode).ConfigureAwait(true);

        return new DurableTurnResult<TTurnState>
        {
            Response = loopResult.Response,
            CompletionReason = loopResult.CompletionReason,
            FinalTurnState = loopResult.FinalTurnState is { } state
                ? state.Deserialize<TTurnState>(DurableAIJsonUtilities.DefaultOptions)
                : default,
        };
    }

    /// <inheritdoc/>
    protected sealed override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        DurableTurnResult<TTurnState> output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);

    /// <inheritdoc/>
    protected sealed override Task<List<DurableSessionEntry>> ApplyKeyedHistoryReducerAsync(
        string reducerKey,
        List<DurableSessionEntry> history,
        ActivityOptions activityOptions) =>
        Workflow.ExecuteActivityAsync(
            (DurableChatActivities activities) => activities.ReduceHistoryByKeyAsync(
                new ReduceHistoryByKeyInput { ReducerKey = reducerKey, History = history }),
            activityOptions);

    /// <inheritdoc/>
    protected sealed override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            Workflow.Info.WorkflowType,
            new object[] { input });
}
