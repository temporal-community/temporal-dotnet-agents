namespace TemporalCommunity.Extensions.AI;

/// <summary>Current Temporal event-history pressure for a durable chat session.</summary>
public sealed record DurableChatHistoryBudgetStatus
{
    /// <summary>Current workflow history event count, as observed at the latest workflow task.</summary>
    public required int HistoryEventCount { get; init; }

    /// <summary>Current workflow history size in bytes, as observed at the latest workflow task.</summary>
    public required int HistorySizeBytes { get; init; }

    /// <summary>Whether new turns are being restricted to prevent additional history growth.</summary>
    public required bool IsAdmissionRestricted { get; init; }

    /// <summary>Whether an approval wait must expire to preserve completion headroom.</summary>
    public required bool IsReserveReached { get; init; }

    /// <summary>Configured event-count threshold for restricting new turns.</summary>
    public required int HistoryEventSoftLimit { get; init; }

    /// <summary>Configured event-count threshold for expiring approval waits.</summary>
    public required int HistoryEventReserveLimit { get; init; }

    /// <summary>Configured history-size threshold in bytes for restricting new turns.</summary>
    public required int HistorySizeSoftLimitBytes { get; init; }

    /// <summary>Configured history-size threshold in bytes for expiring approval waits.</summary>
    public required int HistorySizeReserveLimitBytes { get; init; }

    /// <summary>Maximum permitted matching duplicate handlers for an active turn.</summary>
    public required int MaxConcurrentTurnJoiners { get; init; }

    /// <summary>Number of matching duplicate handlers currently joined to the active turn.</summary>
    public required int ConcurrentTurnJoiners { get; init; }
}
