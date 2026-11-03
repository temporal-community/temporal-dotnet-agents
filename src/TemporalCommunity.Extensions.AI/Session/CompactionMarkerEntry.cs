using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// A durable history entry that replaces one contiguous range with a summary.
/// </summary>
/// <remarks>
/// The <c>"compaction-marker"</c> discriminator is a workflow-history wire constant and must
/// remain stable. The marker stores only the first and last replaced entry/turn IDs and counts;
/// it does not retain an unbounded list of every source ID.
/// </remarks>
[Experimental("TA002")]
public sealed class CompactionMarkerEntry : DurableSessionEntry
{
    /// <summary>The first entry replaced by this summary.</summary>
    public required string FirstReplacedEntryId { get; init; }

    /// <summary>The last entry replaced by this summary.</summary>
    public required string LastReplacedEntryId { get; init; }

    /// <summary>The number of contiguous entries represented by this marker.</summary>
    public required int ReplacedEntryCount { get; init; }

    /// <summary>The first logical turn represented by this marker.</summary>
    public required string FirstReplacedTurnId { get; init; }

    /// <summary>The last logical turn represented by this marker.</summary>
    public required string LastReplacedTurnId { get; init; }

    /// <summary>The number of complete turns represented by this marker.</summary>
    public required int ReplacedTurnCount { get; init; }

    /// <summary>The strategy key that produced the summary.</summary>
    public required string Strategy { get; init; }

    /// <summary>The model used to produce the summary, or empty for a non-LLM strategy.</summary>
    public required string ModelId { get; init; }

    /// <summary>The summary result represented by this marker.</summary>
    public required DurableSessionSummary Summary { get; init; }

    /// <summary>Aliases <see cref="DurableSessionEntry.CreatedAt"/> without another wire field.</summary>
    [JsonIgnore]
    public DateTimeOffset CompactedAt => CreatedAt;
}
