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
    internal const string InvalidRequestErrorType = "DurableTurnInvalidRequest";
    internal const string IterationLimitHistoryPatchId =
        "durable-tool-iteration-limit-history-v1";

    private readonly Dictionary<DurableSessionRequest, DurableTurnRequest<TRequestData, TTurnState>>
        _turnRequests = new(Internal.ReferenceComparer<DurableSessionRequest>.Instance);
    private readonly HashSet<string> _managedUpdateIds = new(StringComparer.Ordinal);
    private bool _toolAuthorityReady;

    /// <summary>
    /// Gets the ordered worker-owned toolsets that form this custom workflow's maximum baseline.
    /// A <see langword="null"/> value uses the worker defaults; an empty list creates a no-tools
    /// baseline. The value must be deterministic and stable for the workflow type.
    /// </summary>
    /// <remarks>
    /// Override with a fixed list when this workflow must use a subset of the worker's registered
    /// toolsets. Do not read dependency injection, process-local state, external services, or
    /// nondeterministic APIs from this property.
    /// </remarks>
    protected virtual IReadOnlyList<string>? DurableToolsetBaselineIds => null;

    /// <inheritdoc/>
    protected sealed override async Task RunAsync(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var authority = Internal.DurableToolsetAuthority.Resolve(input);
        if (authority == Internal.DurableToolsetAuthorityKind.None)
        {
            var baselineIds = DurableToolsetBaselineIds;
            var resolverOptions = new ActivityOptions
            {
                StartToCloseTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                RetryPolicy = Internal.DefaultRetryPolicy.Resolve(input.RetryPolicy),
                Summary = "Resolve durable toolsets",
            };
            var resolutionRequest = baselineIds is null
                ? new Internal.DurableToolsetResolutionRequest { UseWorkerDefaults = true }
                : new Internal.DurableToolsetResolutionRequest { ToolsetIds = baselineIds };
            var manifest = await Workflow.ExecuteActivityAsync(
                (DurableToolsetActivities activities) =>
                    activities.ResolveDurableToolsetsAsync(resolutionRequest),
                resolverOptions).ConfigureAwait(true);
            manifest.Validate();
            input = input with { ToolsetManifest = manifest };
        }
        var sessionTask = base.RunAsync(input);
        _toolAuthorityReady = true;
        await sessionTask.ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one package-managed durable turn. This method must be called from a workflow Update,
    /// and may be called at most once for that Update ID in the current workflow run.
    /// </summary>
    protected async Task<DurableTurnResult<TTurnState>> RunDurableTurnAsync(
        DurableTurnRequest<TRequestData, TTurnState> request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw CreateInvalidRequestFailure("Durable turn request cannot be null.");
        }

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
            throw CreateInvalidRequestFailure("At least one message is required.");
        }

        if (request.Options is null)
        {
            throw CreateInvalidRequestFailure("Turn options cannot be null.");
        }

        if (!Enum.IsDefined(typeof(DurableToolDispatchMode), request.Options.DispatchMode))
        {
            throw new ApplicationFailureException(
                $"Unknown durable tool dispatch mode '{(int)request.Options.DispatchMode}'.",
                errorType: "DurableTurnInvalidDispatchMode",
                nonRetryable: true);
        }

        await Workflow.WaitConditionAsync(() => _toolAuthorityReady).ConfigureAwait(true);

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

    private static ApplicationFailureException CreateInvalidRequestFailure(string message) =>
        new(
            message,
            errorType: InvalidRequestErrorType,
            nonRetryable: true);

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

        var turnManifest = RequiredInput.ToolsetManifest?.Narrow(request.Options.ToolsetIds);
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
            request.Options.DispatchMode,
            turnManifest).ConfigureAwait(true);

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
        DateTimeOffset createdAt)
    {
        var historyResponse = output.Response;
        if (output.CompletionReason == DurableTurnCompletionReason.IterationLimitReached &&
            Workflow.Patched(IterationLimitHistoryPatchId))
        {
            // The caller still receives the complete response, including the executed function
            // protocol. Only persisted conversation history is reduced. This prevents a later
            // turn from observing successful tool results whose typed turn state the caller
            // intentionally discarded after the iteration limit was reached.
            historyResponse = new ChatResponse([output.Response.Messages[^1]])
            {
                Usage = output.Response.Usage,
            };
        }

        return DurableSessionResponse.FromChatResponse(
            correlationId,
            historyResponse,
            createdAt);
    }

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
