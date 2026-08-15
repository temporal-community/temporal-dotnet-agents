// OrderInterceptor — demonstrates IAgentToolInterceptor (Feature L).
//
// Only fires for apply_refund. All other tools pass through with Proceed().
//
// Decision logic for apply_refund:
//   • Order not found          → Skip  (synthetic "not found" result; tool never dispatches)
//   • Amount > 500             → Block (policy violation; tool never dispatches)
//   • Everything else          → PauseForApproval (enriched description for the reviewer)

using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Tools;

namespace ToolInterceptor;

/// <summary>
/// Pre-tool interceptor that enforces refund policy before <c>apply_refund</c>
/// is dispatched as a Temporal activity.
/// Implements <see cref="IAgentToolInterceptor"/> — the MAF-specific sub-interface
/// that provides <see cref="AgentToolContext"/> with <c>AgentName</c> and
/// <c>StateBag</c> in addition to the shared base fields.
/// </summary>
public sealed class OrderInterceptor(FakeOrderService orderService) : IAgentToolInterceptor
{
    private const string RefundTool = "apply_refund";
    private const decimal AutoApprovalLimit = 500m;

    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        // All tools other than apply_refund proceed without inspection.
        if (!string.Equals(context.ToolName, RefundTool, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(DurableToolDecision.Proceed());

        // Extract order_id argument.
        var orderId = context.Arguments.TryGetValue("orderId", out var id)
            ? id?.ToString()
            : null;

        // Fallback: some models may emit snake_case keys.
        if (string.IsNullOrWhiteSpace(orderId))
        {
            orderId = context.Arguments.TryGetValue("order_id", out var id2)
                ? id2?.ToString()
                : null;
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Task.FromResult(
                DurableToolDecision.Skip("Order ID was not provided. No refund was processed."));
        }

        // Decision path 1 — order not found → Skip.
        var order = orderService.GetOrder(orderId);
        if (order is null)
        {
            return Task.FromResult(
                DurableToolDecision.Skip($"Order {orderId} was not found. No refund was processed."));
        }

        // Extract amount argument.
        decimal amount = 0m;
        if (context.Arguments.TryGetValue("amount", out var amtObj) && amtObj is not null)
        {
            decimal.TryParse(amtObj.ToString(), out amount);
        }

        // Decision path 2 — large amount → Block (policy violation).
        if (amount > AutoApprovalLimit)
        {
            return Task.FromResult(
                DurableToolDecision.Block(
                    $"Refund amount ${amount:F2} exceeds the ${AutoApprovalLimit:F0} " +
                    $"automatic approval limit. Please contact a supervisor."));
        }

        // Decision path 3 — valid refund → PauseForApproval with reviewer-safe metadata.
        // This is display context only. It is not a reviewer credential or authorization claim;
        // the write tool must still authorize against authoritative state before the effect.
        var description =
            $"Apply refund of ${amount:F2} for {order.CustomerName} " +
            $"— {order.ProductName}, Order {order.OrderId}";

        return Task.FromResult(DurableToolDecision.PauseForApproval(
            description,
            new Dictionary<string, string>
            {
                ["orderId"] = order.OrderId,
                ["refundAmount"] = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                ["policy"] = "refund-under-500-review",
            }));
    }
}
