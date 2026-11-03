namespace TemporalCommunity.Extensions.AI;

/// <summary>Explicit retained-history strategy applied at safe continue-as-new boundaries.</summary>
public enum DurableHistoryCompactionStrategy
{
    /// <summary>Do not rewrite retained history automatically.</summary>
    None,

    /// <summary>Replace an oldest complete-turn prefix with a library-generated summary.</summary>
    SummarizeOldestTurns,

    /// <summary>Remove an oldest complete-turn prefix and retain a truncation marker.</summary>
    TrimRecentTurns,
}

/// <summary>Durable recovery state for a history-compaction incident.</summary>
public sealed record DurableHistoryRecoveryState
{
    /// <summary>Whether new turns are blocked pending a successful recovery operation.</summary>
    public bool IsBlocked { get; init; }

    /// <summary>Bounded, content-free reason the session entered the blocked state.</summary>
    public string? BlockReason { get; init; }

    /// <summary>Accepted retry operations in this blocked incident, across continue-as-new.</summary>
    public int RetryCountThisIncident { get; init; }

    /// <summary>Whether the explicitly authorized trim was accepted in this incident.</summary>
    public bool TrimUsedThisIncident { get; init; }

    /// <summary>Distinct accepted recovery operation IDs retained for duplicate rejection.</summary>
    public IReadOnlyList<string> AcceptedOperationIds { get; init; } = [];

    /// <summary>Content-free outcome of the last accepted recovery operation.</summary>
    public string? LastOutcome { get; init; }
}

/// <summary>Result of an accepted manual history-recovery operation.</summary>
public sealed record DurableHistoryRecoveryResult
{
    /// <summary>The caller-supplied idempotency key for this operation.</summary>
    public required string OperationId { get; init; }

    /// <summary>Whether the session remains blocked after the operation.</summary>
    public required bool IsBlocked { get; init; }

    /// <summary>Content-free outcome label.</summary>
    public required string Outcome { get; init; }
}
