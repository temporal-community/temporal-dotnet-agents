namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// Retained-history marker describing a range removed by an explicitly selected trim strategy.
/// It is distinct from a summarization marker: it records that content was removed, not a
/// replacement summary.
/// </summary>
public sealed class TruncationMarkerEntry : DurableSessionEntry
{
    /// <summary>The first removed entry in the contiguous range.</summary>
    public required string FirstRemovedEntryId { get; init; }

    /// <summary>The last removed entry in the contiguous range.</summary>
    public required string LastRemovedEntryId { get; init; }

    /// <summary>The number of entries removed from the range.</summary>
    public required int RemovedEntryCount { get; init; }

    /// <summary>The first logical turn touched by the removed range.</summary>
    public required string FirstRemovedTurnId { get; init; }

    /// <summary>The last logical turn touched by the removed range.</summary>
    public required string LastRemovedTurnId { get; init; }

    /// <summary>The number of complete turns removed from the range.</summary>
    public required int RemovedTurnCount { get; init; }
}
