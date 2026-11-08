using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI;

/// <summary>A completed durable chat request retained in the bounded terminal ledger.</summary>
public sealed record DurableChatTerminalOutcome
{
    /// <summary>The logical request ID supplied to the turn.</summary>
    public required string RequestId { get; init; }

    /// <summary>The retained assistant response for a successful turn, or null on failure.</summary>
    public DurableSessionResponse? Response { get; init; }

    /// <summary>The bounded failure type for a failed turn, or null on success.</summary>
    public string? FailureErrorType { get; init; }

    /// <summary>The bounded failure message for a failed turn, or null on success.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// Whether the retained failure is non-retryable, or null when no failure classification
    /// is retained. A retryable classification does not make external effects safe to repeat.
    /// </summary>
    public bool? FailureNonRetryable { get; init; }
}
