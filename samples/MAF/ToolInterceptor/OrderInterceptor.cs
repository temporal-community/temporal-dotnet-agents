// OrderInterceptor — demonstrates IDurableToolInterceptor<DurableToolContext>.
//
// This interceptor is intentionally typed against the BASE context (DurableToolContext),
// not the MAF-specific IAgentToolInterceptor. It only needs ToolName and Arguments —
// both are base context fields — so there is no reason to reach for the richer
// AgentToolContext. This also means the same class could be registered in a MEAI
// session (Phase 2) without modification.
//
// Use IAgentToolInterceptor / AgentToolContext only when you need:
//   • context.AgentName  — to distinguish behaviour by registered agent
//   • context.StateBag   — to read/carry MAF session state across turns
//
// Decision logic for apply_refund:
//   • Order not found          → Skip  (synthetic "not found" result; tool never dispatches)
//   • Amount > 500             → Block (policy violation; tool never dispatches)
//   • Everything else          → PauseForApproval (enriched description for the reviewer)

using Temporalio.Extensions.AI;

namespace ToolInterceptor;

/// <summary>
/// Pre-tool interceptor that enforces refund policy before <c>apply_refund</c>
/// is dispatched as a Temporal activity.
/// Implements <see cref="IDurableToolInterceptor{TContext}"/> with the base
/// <see cref="DurableToolContext"/> — no MAF-specific fields required.
/// </summary>
public sealed class OrderInterceptor(FakeOrderService orderService)
    : IDurableToolInterceptor<DurableToolContext>
{
    private const string RefundTool = "apply_refund";
    private const decimal AutoApprovalLimit = 500m;

    public Task<DurableToolDecision> BeforeToolCallAsync(
        DurableToolContext context,
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

        // Decision path 3 — valid refund → PauseForApproval with enriched description.
        var description =
            $"Apply refund of ${amount:F2} for {order.CustomerName} " +
            $"— {order.ProductName}, Order {order.OrderId}";

        return Task.FromResult(DurableToolDecision.PauseForApproval(description));
    }
}
