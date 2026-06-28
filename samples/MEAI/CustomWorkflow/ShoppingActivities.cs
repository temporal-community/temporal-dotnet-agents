using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using TemporalCommunity.Extensions.AI;

namespace CustomWorkflow;

/// <summary>
/// Temporal activities for the shopping assistant workflow.
/// Executes chat turns via the injected <see cref="IChatClient"/> and collects
/// cart mutation actions produced by tool calls during the LLM response.
/// </summary>
internal sealed class ShoppingActivities(
    IChatClient chatClient,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<ShoppingActivities>();

    /// <summary>
    /// Executes a shopping assistant chat turn.
    /// Injects cart tools into <see cref="ChatOptions"/> so the LLM can call them,
    /// then collects the resulting <see cref="CartAction"/> records and returns them
    /// alongside the <see cref="ChatResponse"/>.
    /// </summary>
    [Activity("CustomWorkflow.GetShoppingResponse")]
    public async Task<ShoppingTurnOutput> GetShoppingResponseAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        _logger.LogDebug(
            "Executing shopping activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        // Collect cart actions produced by the tools during this turn.
        var cartActions = new List<CartAction>();

        // Define cart tools that close over cartActions so mutations are captured.
        var addToCart = AIFunctionFactory.Create(
            (string productId, string productName, int quantity) =>
            {
                cartActions.Add(new CartAction
                {
                    ProductId = productId,
                    ProductName = productName,
                    Quantity = quantity,
                    Action = "add",
                });
                return $"Added {quantity}x {productName} (SKU: {productId}) to the cart.";
            },
            name: "add_to_cart",
            description: "Add a product to the shopping cart.");

        var removeFromCart = AIFunctionFactory.Create(
            (string productId) =>
            {
                var existing = cartActions.FirstOrDefault(a => a.ProductId == productId);
                cartActions.Add(new CartAction
                {
                    ProductId = productId,
                    ProductName = existing?.ProductName ?? productId,
                    Action = "remove",
                });
                return $"Removed product {productId} from the cart.";
            },
            name: "remove_from_cart",
            description: "Remove a product from the shopping cart by product ID.");

        // Clone the caller's ChatOptions so every field (ModelId, Seed, StopSequences,
        // ResponseFormat, AdditionalProperties, Instructions, etc.) is preserved, then
        // overwrite Tools with the cart tools the LLM needs to see this turn.
        // ChatOptions.Clone() is the MEAI-supplied shallow-copy constructor.
        var options = input.Options?.Clone() ?? new ChatOptions();
        options.Tools = [addToCart, removeFromCart];
        options.ToolMode ??= ChatToolMode.Auto;

        // The Temporal SDK does NOT auto-heartbeat. The LLM + UseFunctionInvocation() tool
        // loop inside GetResponseAsync can easily exceed the default 2-minute HeartbeatTimeout.
        // Run a background task that heartbeats every 30 seconds (well under the default)
        // for the duration of the call. Same pattern as samples/MEAI/HumanInTheLoop.
        using var hbCts = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            while (!hbCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), hbCts.Token); }
                catch (OperationCanceledException) { break; }
                if (!hbCts.Token.IsCancellationRequested)
                    ctx.Heartbeat($"turn-{input.TurnNumber}");
            }
        }, hbCts.Token);

        ChatResponse response;
        try
        {
            response = await chatClient.GetResponseAsync(
                input.Messages,
                options,
                ct).ConfigureAwait(false);
        }
        finally
        {
            await hbCts.CancelAsync();
            await heartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _logger.LogDebug(
            "Shopping activity completed for conversation {ConversationId}, turn {TurnNumber}. CartActions: {CartActionCount}",
            input.ConversationId, input.TurnNumber, cartActions.Count);

        return new ShoppingTurnOutput
        {
            Response = response,
            CartActions = cartActions,
        };
    }
}
