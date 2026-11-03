#pragma warning disable TA002 // marker type is experimental but referenced by-name in tests

using System.Text.Json;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Pins that the Agents-side <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>
/// round-trips the compaction and truncation markers via their polymorphic base types. The
/// discriminator is declared inline on <see cref="DurableSessionEntry"/> so both
/// source-gen contexts see it for free — this test is the contract pin that ensures the
/// Agents-side context didn't silently lose it.
/// </summary>
public class CompactionMarkerAgentContextTests
{
    [Fact]
    public void Marker_RoundTrips_UnderTemporalAgentsContext()
    {
        var original = new CompactionMarkerEntry
        {
            CorrelationId = "marker-agent-1",
            CreatedAt = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            FirstReplacedEntryId = "agent-entry-1",
            LastReplacedEntryId = "agent-entry-2",
            ReplacedEntryCount = 2,
            FirstReplacedTurnId = "agent-turn-x",
            LastReplacedTurnId = "agent-turn-x",
            ReplacedTurnCount = 1,
            Strategy = "sliding-window",
            ModelId = string.Empty, // non-LLM strategy
            Summary = new DurableSessionSummary { Text = "retained summary" },
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            original, TemporalAgentJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<DurableSessionEntry>(
            json, TemporalAgentJsonUtilities.DefaultOptions);

        var marker = Assert.IsType<CompactionMarkerEntry>(back);
        Assert.Equal(original.CorrelationId, marker.CorrelationId);
        Assert.Equal(original.Strategy, marker.Strategy);
        Assert.Equal(original.FirstReplacedEntryId, marker.FirstReplacedEntryId);
        Assert.Equal(original.LastReplacedEntryId, marker.LastReplacedEntryId);
        Assert.Equal(original.ReplacedEntryCount, marker.ReplacedEntryCount);
        Assert.Equal(original.FirstReplacedTurnId, marker.FirstReplacedTurnId);
        Assert.Equal(original.LastReplacedTurnId, marker.LastReplacedTurnId);
        Assert.Equal(original.ReplacedTurnCount, marker.ReplacedTurnCount);
        Assert.Equal(original.Summary.Text, marker.Summary.Text);
    }

    [Fact]
    public void Marker_DiscriminatorPinned_UnderAgentsContext()
    {
        var marker = new CompactionMarkerEntry
        {
            CorrelationId = "marker-agent-2",
            CreatedAt = DateTimeOffset.UtcNow,
            FirstReplacedEntryId = "x",
            LastReplacedEntryId = "x",
            ReplacedEntryCount = 1,
            FirstReplacedTurnId = "t",
            LastReplacedTurnId = "t",
            ReplacedTurnCount = 1,
            Strategy = "truncation",
            ModelId = string.Empty,
            Summary = new DurableSessionSummary { Text = "removed history" },
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            marker, TemporalAgentJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("compaction-marker", doc.RootElement.GetProperty("$type").GetString());
    }

    [Fact]
    public void TruncationMarker_RoundTrips_UnderTemporalAgentsContext()
    {
        DurableSessionEntry original = new TruncationMarkerEntry
        {
            CorrelationId = "truncate-agent-1",
            CreatedAt = DateTimeOffset.UtcNow,
            FirstRemovedEntryId = "entry-1",
            LastRemovedEntryId = "entry-2",
            RemovedEntryCount = 2,
            FirstRemovedTurnId = "turn-1",
            LastRemovedTurnId = "turn-1",
            RemovedTurnCount = 1,
        };

        var json = JsonSerializer.Serialize(original, TemporalAgentJsonUtilities.DefaultOptions);
        var back = Assert.IsType<TruncationMarkerEntry>(
            JsonSerializer.Deserialize<DurableSessionEntry>(json, TemporalAgentJsonUtilities.DefaultOptions));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("truncation-marker", doc.RootElement.GetProperty("$type").GetString());
        Assert.Equal(2, back.RemovedEntryCount);
    }
}
