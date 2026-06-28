#pragma warning disable TA002 // marker type is experimental but referenced by-name in tests

using System.Text.Json;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Step 5a: pins that the Agents-side <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>
/// round-trips a <see cref="CompactionMarkerEntry"/> via its polymorphic base type. The
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
            CompactedMessageIds = new[] { "agent-msg-1", "agent-msg-2" },
            Strategy = "sliding-window",
            ModelId = string.Empty, // non-LLM strategy
            OriginatingTurnCorrelationIds = new[] { "agent-turn-x" },
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            original, TemporalAgentJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<DurableSessionEntry>(
            json, TemporalAgentJsonUtilities.DefaultOptions);

        var marker = Assert.IsType<CompactionMarkerEntry>(back);
        Assert.Equal(original.CorrelationId, marker.CorrelationId);
        Assert.Equal(original.Strategy, marker.Strategy);
        Assert.Equal(original.CompactedMessageIds, marker.CompactedMessageIds);
        Assert.Equal(original.OriginatingTurnCorrelationIds, marker.OriginatingTurnCorrelationIds);
    }

    [Fact]
    public void Marker_DiscriminatorPinned_UnderAgentsContext()
    {
        var marker = new CompactionMarkerEntry
        {
            CorrelationId = "marker-agent-2",
            CreatedAt = DateTimeOffset.UtcNow,
            CompactedMessageIds = new[] { "x" },
            Strategy = "truncation",
            ModelId = string.Empty,
            OriginatingTurnCorrelationIds = new[] { "t" },
        };

        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            marker, TemporalAgentJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("compaction-marker", doc.RootElement.GetProperty("$type").GetString());
    }
}
