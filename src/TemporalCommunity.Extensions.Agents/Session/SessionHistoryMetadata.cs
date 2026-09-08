using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// Internal metadata tracking session history state (entry count, last compaction, etc.).
/// Carried forward across continue-as-new to guide history reduction decisions.
/// </summary>
internal sealed class SessionHistoryMetadata
{
    /// <summary>Gets the timestamp of the last compaction marker entry.</summary>
    [JsonPropertyName("lastCompactionTime")]
    public DateTimeOffset? LastCompactionTime { get; init; }

    /// <summary>Gets the correlation ID of the compaction request.</summary>
    [JsonPropertyName("lastCompactionCorrelationId")]
    public string? LastCompactionCorrelationId { get; init; }

    /// <summary>
    /// Gets the total entry count at the time of last compaction.
    /// Used to detect when compaction threshold is reached again.
    /// </summary>
    [JsonPropertyName("entriesAtLastCompaction")]
    public int? EntriesAtLastCompaction { get; init; }
}
