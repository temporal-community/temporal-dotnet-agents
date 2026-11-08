using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>The availability and terminal state of an agent turn identified by request ID.</summary>
public enum TemporalAgentTurnOutcomeStatus
{
    /// <summary>The request is currently executing in the agent workflow.</summary>
    Pending,

    /// <summary>The request completed successfully.</summary>
    Succeeded,

    /// <summary>The request completed with a failure.</summary>
    Failed,

    /// <summary>No active or retained outcome is available for this request ID.</summary>
    Unavailable,
}

/// <summary>A read-only snapshot of an agent turn's current or retained outcome.</summary>
public sealed record TemporalAgentTurnOutcome
{
    /// <summary>The logical request ID supplied when the turn was started.</summary>
    public required string RequestId { get; init; }

    /// <summary>The turn state, or <see cref="TemporalAgentTurnOutcomeStatus.Unavailable"/>.</summary>
    public required TemporalAgentTurnOutcomeStatus Status { get; init; }

    /// <summary>The retained response for a successful turn, when response details were retained.</summary>
    public DurableSessionResponse? Response { get; init; }

    /// <summary>The bounded failure type for a failed turn, when failure details were retained.</summary>
    public string? FailureErrorType { get; init; }

    /// <summary>The bounded failure message for a failed turn, when failure details were retained.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// Whether the retained failure is non-retryable, or null when no failure classification
    /// is retained. A retryable classification does not make external effects safe to repeat.
    /// </summary>
    public bool? FailureNonRetryable { get; init; }

    /// <summary>The last committed session-state revision, when retained.</summary>
    public long? CommittedRevision { get; init; }

    /// <summary>Bounded, result-free per-call identities and statuses, when retained.</summary>
    public IReadOnlyList<DurableToolCallOutcomeSummary>? ToolCallSummaries { get; init; }

    /// <summary>Whether any tool-call identities were omitted from the bounded summaries.</summary>
    public bool ToolCallSummariesTruncated { get; init; }

    /// <summary>Per-status counts for all calls, including calls whose identities were omitted.</summary>
    public DurableToolCallOutcomeCounts? ToolCallCounts { get; init; }

    /// <summary>
    /// Whether terminal response/failure details or tool-call identities were omitted to fit the
    /// bounded ledger. False does not mean external tool effects were rolled back or safe to repeat.
    /// </summary>
    public bool DetailsOmitted { get; init; }
}
