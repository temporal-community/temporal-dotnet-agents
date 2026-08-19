using Microsoft.Extensions.AI;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Workflows;

namespace CustomWorkflow;

/// <summary>
/// Durable shopping assistant workflow.
/// Extends <see cref="DurableChatWorkflowBase{TOutput}"/> with <see cref="ShoppingTurnOutput"/>
/// so each Update returns both the assistant response and the list of cart actions
/// that occurred during the LLM tool calls in that turn.
/// </summary>
[Workflow("CustomWorkflow.ShoppingAssistant")]
public sealed class ShoppingAssistantWorkflow : DurableChatWorkflowBase<ShoppingTurnOutput>
{
    // Per-turn metadata keyed by correlation ID. Populated in ShopAsync before
    // dispatching to RunTurnAsync; read inside ExecuteTurnAsync and removed after use.
    // Keying by correlation ID (vs. a scalar field) avoids the race where a second
    // Shop update overwrites the first turn's value while it is still awaiting its activity.
    private readonly Dictionary<string, string> _conversationIdByCorrelation = new();

    [WorkflowRun]
    public new async Task RunAsync(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InitializeInput(input);
        await base.RunAsync(input).ConfigureAwait(true);
    }

    /// <summary>
    /// Validates a shopping turn request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ShopAsync))]
    public void ValidateShop(DurableChatInput input)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input?.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a shopping assistant turn and returns the response along with
    /// any cart mutations that occurred during tool calls in this turn.
    /// </summary>
    [WorkflowUpdate("Shop")]
    public async Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input)
    {
        // Build the request entry — factory auto-generates the correlation ID via
        // Workflow.NewGuid() when the caller did not supply one.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(messages, input.CorrelationId);

        // Stash per-turn metadata keyed by the correlation ID so concurrent Shop updates
        // cannot stomp on each other's values while waiting on the _isProcessing mutex.
        if (!string.IsNullOrEmpty(input.ConversationId))
        {
            _conversationIdByCorrelation[requestEntry.CorrelationId] = input.ConversationId;
        }

        try
        {
            var (output, _) = await RunTurnAsync(requestEntry, input.Options);
            return output;
        }
        finally
        {
            _conversationIdByCorrelation.Remove(requestEntry.CorrelationId);
        }
    }

    /// <summary>
    /// Wraps the shopping turn output's <see cref="ChatResponse"/> into a
    /// <see cref="DurableSessionResponse"/> for history storage. Cart-action data is
    /// retained on the live <see cref="ShoppingTurnOutput"/> returned by <see cref="ShopAsync"/>;
    /// only the chat response is persisted in the durable session history.
    /// </summary>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ShoppingTurnOutput output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);

    protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Flatten the entire history (including the just-appended request entry) into a
        // single message list so the LLM sees the full conversation each turn.
        var activityMessages = History
            .SelectMany(e => e.Messages)
            .ToList();

        var conversationId = _conversationIdByCorrelation.TryGetValue(
            requestEntry.CorrelationId, out var stashed)
                ? stashed
                : Workflow.Info.WorkflowId;

        var activityInput = new DurableChatInput
        {
            Messages = activityMessages,
            Options = chatOptions,
            ConversationId = conversationId,
            TurnNumber = CurrentTurnNumber,
            CorrelationId = requestEntry.CorrelationId,
        };
        // Sample-specific hardening: this activity wraps non-idempotent cart mutations
        // (add_to_cart / remove_from_cart closures inside GetShoppingResponseAsync) plus a
        // non-deterministic LLM call. The base class leaves RetryPolicy unset, which lets the
        // Temporal server default (retry forever) apply — that would re-invoke the LLM on any
        // transient failure and risk duplicating cart side effects. Cap attempts at 1 so the
        // turn fails fast and the caller can re-issue the Shop update explicitly.
        // NOT a general rule: idempotent activities should keep the default retry behavior.
        var hardened = (ActivityOptions)activityOptions.Clone();
        hardened.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        return Workflow.ExecuteActivityAsync(
            (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
            hardened);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
}
