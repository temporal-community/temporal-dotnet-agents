// FakeOrderService — in-memory order store used by the ToolInterceptor sample.
// Three known orders cover the three interceptor decision paths demonstrated in this sample.

using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace ToolInterceptor;

/// <summary>Represents a known order in the fake store.</summary>
public sealed record OrderRecord(
    string OrderId,
    string CustomerName,
    string ProductName,
    decimal Amount,
    bool AlreadyRefunded);

/// <summary>
/// In-memory order store. Register as a singleton so the interceptor and the
/// tool implementations share the same instance.
/// </summary>
public sealed class FakeOrderService
{
    private static readonly Dictionary<string, OrderRecord> s_orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ORD-001"] = new("ORD-001", "Jane Doe",    "MacBook Pro",         29.99m, false),
        ["ORD-002"] = new("ORD-002", "John Smith",  "Enterprise License", 750.00m, false),
        ["ORD-003"] = new("ORD-003", "Alice Brown",  "USB-C Hub",          19.99m, true),
    };

    /// <summary>Returns order details, or <see langword="null"/> when the order ID is unknown.</summary>
    public OrderRecord? GetOrder(string orderId) =>
        s_orders.TryGetValue(orderId, out var o) ? o : null;

    // ── AI tools ──────────────────────────────────────────────────────────────

    [Description("Look up an order by its order ID and return a summary of its details.")]
    public string LookupOrder(
        [Description("The order ID to look up, e.g. ORD-001")] string orderId)
    {
        var order = GetOrder(orderId);
        if (order is null) return $"Order {orderId} was not found.";

        var status = order.AlreadyRefunded ? "already refunded" : "eligible for refund";
        return $"Order {order.OrderId}: {order.CustomerName}, {order.ProductName}, " +
               $"${order.Amount:F2} — {status}.";
    }

    [Description("Apply a refund of the specified amount to the given order. WRITE — non-idempotent.")]
    public string ApplyRefund(
        [Description("The order ID to refund")] string orderId,
        [Description("The refund amount in USD")] decimal amount)
    {
        // In a real system this would call the payments API.
        return $"Refund of ${amount:F2} for order {orderId} has been applied successfully.";
    }
}
