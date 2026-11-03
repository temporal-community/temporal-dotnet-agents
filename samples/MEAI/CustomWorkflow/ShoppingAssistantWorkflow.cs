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
    // Per-turn metadata keyed by correlation ID. Populated before dispatching the turn;
    // read inside ExecuteTurnAsync and removed after use.
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
        ValidateTurnAdmission(input);
    }

    /// <summary>
    /// Executes a shopping assistant turn and returns the response along with
    /// any cart mutations that occurred during tool calls in this turn.
    /// </summary>
    [WorkflowUpdate("Shop")]
    public async Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input)
    {
        var admission = BeginTurnAdmission(input);
        // Build the request entry — logical request identity is distinct from correlation metadata.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(
            messages,
            input.CorrelationId,
            requestId: admission.RequestId);

        // Stash per-turn metadata keyed by correlation ID for the admitted owner.
        if (!string.IsNullOrEmpty(input.ConversationId))
        {
            _conversationIdByCorrelation[requestEntry.CorrelationId] = input.ConversationId;
        }

        try
        {
            var (output, _) = await RunTurnAsync(requestEntry, admission, input.Options);
            return output;
        }
        catch (Exception exception)
        {
            FailTurnAdmission(admission, exception);
            throw;
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
        string? requestId,
        ShoppingTurnOutput output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(
            correlationId,
            output.Response,
            createdAt,
            DurableTurnCompletionReason.FinalResponse,
            requestId);

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
        // non-deterministic LLM call. The base class uses a bounded five-attempt
        // retry policy, which could duplicate cart side effects. Cap attempts at 1 so the
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
