// Tools.cs — tool services for the one-attempt-default and serial-dispatch sample.
//
// All three are registered as DI singletons. Each method becomes an AIFunction via
// AIFunctionFactory.Create and is registered with the agent via agent.AddTool(). The
// durable-agent loop dispatches each tool call as a separate InvokeAgentTool activity;
// retries are configured at agent level, not per tool.

using System.Collections.Frozen;
using System.ComponentModel;

namespace PerToolActivities;

/// <summary>
/// Read-style order-status tool. It uses the one-attempt default. An agent-level retry
/// policy would also apply to every other tool registered on that agent.
/// <para>
/// <see cref="FailOnceEnabled"/> is the demo toggle for scenario 2: when set, the
/// first call throws so we can observe the tool activity fail after one attempt and
/// later calls from the same model response remain undispatched.
/// </para>
/// </summary>
public sealed class OrderService
{
    private static readonly FrozenDictionary<string, string> _orders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ORD-001"] = "Shipped — estimated delivery in 2 days",
            ["ORD-002"] = "Delivered on April 28",
            ["ORD-003"] = "Processing — not yet shipped",
            ["ORD-004"] = "Delayed — carrier exception reported",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private int _lookupCalls;
    private int _failArmed;

    /// <summary>
    /// When set to <see langword="true"/>, arms a one-shot latch: the next call to
    /// <see cref="LookupOrder"/> consumes the latch and throws, and every subsequent
    /// call succeeds until the latch is re-armed. Used by the second demo scenario to
    /// prove that the lookup activity retries on its own without re-running write tools.
    /// </summary>
    /// <remarks>
    /// The latch is a self-clearing flag rather than a "first call ever" predicate so
    /// the behavior is independent of how many calls earlier scenarios have made on the
    /// same singleton instance. Setting <see langword="false"/> disarms the latch
    /// without triggering a throw.
    /// </remarks>
    public bool FailOnceEnabled
    {
        get => Volatile.Read(ref _failArmed) == 1;
        set => Interlocked.Exchange(ref _failArmed, value ? 1 : 0);
    }

    /// <summary>How many times <see cref="LookupOrder"/> has been invoked total (across attempts).</summary>
    public int LookupCalls => Volatile.Read(ref _lookupCalls);

    [Description("Look up the current status of a customer order by order ID.")]
    public string LookupOrder([Description("Order ID, e.g. ORD-001")] string orderId)
    {
        Interlocked.Increment(ref _lookupCalls);

        // Atomically consume the one-shot latch. If armed, swap to disarmed and throw;
        // The default tool policy gives this activity one attempt. The latch remains
        // available only if a later model turn deliberately calls the tool again.
        if (Interlocked.CompareExchange(ref _failArmed, 0, 1) == 1)
        {
            throw new InvalidOperationException("Lookup failed before returning a result.");
        }

        return _orders.TryGetValue(orderId, out var status)
            ? status
            : $"Order '{orderId}' not found.";
    }
}

/// <summary>
/// Write-style refund tool. Non-idempotent: an explicitly configured agent retry policy
/// could issue a second refund after an ambiguous failure. Use downstream idempotency.
/// </summary>
public sealed class RefundService
{
    private int _refundCalls;

    /// <summary>How many times <see cref="ApplyRefund"/> has been invoked total.</summary>
    public int RefundCalls => Volatile.Read(ref _refundCalls);

    [Description("Apply a refund of the specified amount to the order. WRITE — do not retry.")]
    public string ApplyRefund(
        [Description("Order ID to refund, e.g. ORD-001")] string orderId,
        [Description("Refund amount in USD")] decimal amount)
    {
        Interlocked.Increment(ref _refundCalls);
        return $"Refund of ${amount:F2} applied to {orderId}. Confirmation: REF-{Random.Shared.Next(10000, 99999)}";
    }
}

/// <summary>
/// Write-style email tool. Non-idempotent: an explicitly configured agent retry policy
/// could deliver a duplicate after an ambiguous failure. Use downstream idempotency.
/// </summary>
public sealed class EmailService
{
    private int _sendCalls;

    /// <summary>How many times <see cref="SendEmail"/> has been invoked total.</summary>
    public int SendCalls => Volatile.Read(ref _sendCalls);

    [Description("Send an email to the customer. WRITE — do not retry.")]
    public string SendEmail(
        [Description("Recipient email address")] string toEmail,
        [Description("Email subject line")] string subject,
        [Description("Email body text")] string body)
    {
        Interlocked.Increment(ref _sendCalls);
        // In production this would call SMTP / SendGrid / SES with `body`. The sample
        // returns a deterministic string so the agent has a confirmation to summarize.
        var preview = body.Length > 40 ? body[..40] + "..." : body;
        return $"Email sent to {toEmail}. Subject: {subject}. Body preview: {preview}";
    }
}
