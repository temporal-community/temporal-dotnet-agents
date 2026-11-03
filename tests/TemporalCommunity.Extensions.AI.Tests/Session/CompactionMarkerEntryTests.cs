#pragma warning disable TA002 // marker type is experimental but referenced by-name in tests

using System.Text.Json;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.AI.Tests.Compat;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Session;

/// <summary>
/// Pins marker wire formats, discriminator values, and source-generated serialization.
/// </summary>
public class CompactionMarkerEntryTests
{
    [Fact]
    public void Marker_RoundTrips_UnderAIContext()
    {
        // Round-trip via DurableSessionEntry base type (forces polymorphic dispatch).
        // If the discriminator registration is correct, the materialized object is a
        // CompactionMarkerEntry with all fields intact.
        var original = NewMarker();

        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            original, DurableAIJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<DurableSessionEntry>(
            json, DurableAIJsonUtilities.DefaultOptions);

        var marker = Assert.IsType<CompactionMarkerEntry>(back);
        Assert.Equal(original.CorrelationId, marker.CorrelationId);
        Assert.Equal(original.FirstReplacedEntryId, marker.FirstReplacedEntryId);
        Assert.Equal(original.LastReplacedEntryId, marker.LastReplacedEntryId);
        Assert.Equal(original.ReplacedEntryCount, marker.ReplacedEntryCount);
        Assert.Equal(original.FirstReplacedTurnId, marker.FirstReplacedTurnId);
        Assert.Equal(original.LastReplacedTurnId, marker.LastReplacedTurnId);
        Assert.Equal(original.ReplacedTurnCount, marker.ReplacedTurnCount);
        Assert.Equal(original.Strategy, marker.Strategy);
        Assert.Equal(original.ModelId, marker.ModelId);
        Assert.Equal(original.Summary.Text, marker.Summary.Text);
        // CompactedAt aliases CreatedAt — never duplicated on the wire.
        Assert.Equal(marker.CreatedAt, marker.CompactedAt);
    }

    [Fact]
    public void Marker_DiscriminatorIsPinned_ToCompactionMarker()
    {
        // The discriminator value is a wire-format constant burned into workflow history.
        // Pinning it here so any accidental rename in DurableSessionEntry.cs trips the test.
        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            NewMarker(), DurableAIJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        var discriminator = doc.RootElement.GetProperty("$type").GetString();
        Assert.Equal("compaction-marker", discriminator);
    }

    [Fact]
    public void Marker_CompactedAt_IsNotDuplicatedOnWire()
    {
        // CompactedAt is a [JsonIgnore] computed alias of CreatedAt. The wire JSON must
        // contain only a single timestamp field for the marker's creation time.
        var json = JsonSerializer.Serialize<DurableSessionEntry>(
            NewMarker(), DurableAIJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("createdAt", out _));
        Assert.False(doc.RootElement.TryGetProperty("compactedAt", out _));
    }

    [Fact]
    public void TruncationMarker_RoundTrips_WithDistinctPinnedDiscriminator()
    {
        DurableSessionEntry original = new TruncationMarkerEntry
        {
            CorrelationId = "truncation-1",
            CreatedAt = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            FirstRemovedEntryId = "entry-1",
            LastRemovedEntryId = "entry-6",
            RemovedEntryCount = 6,
            FirstRemovedTurnId = "turn-1",
            LastRemovedTurnId = "turn-3",
            RemovedTurnCount = 3,
        };

        var json = JsonSerializer.Serialize(original, DurableAIJsonUtilities.DefaultOptions);
        var marker = Assert.IsType<TruncationMarkerEntry>(
            JsonSerializer.Deserialize<DurableSessionEntry>(json, DurableAIJsonUtilities.DefaultOptions));

        Assert.Equal("truncation-marker", JsonDocument.Parse(json).RootElement.GetProperty("$type").GetString());
        Assert.Equal("entry-1", marker.FirstRemovedEntryId);
        Assert.Equal("entry-6", marker.LastRemovedEntryId);
        Assert.Equal(6, marker.RemovedEntryCount);
        Assert.Equal("turn-1", marker.FirstRemovedTurnId);
        Assert.Equal("turn-3", marker.LastRemovedTurnId);
        Assert.Equal(3, marker.RemovedTurnCount);
    }

    [Fact]
    public void SessionSummary_RoundTripsWithUsageMetadata()
    {
        var summary = new DurableSessionSummary
        {
            Text = "bounded summary",
            ModelId = "summary-model",
            Usage = new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 12, OutputTokenCount = 4 },
        };

        var json = JsonSerializer.Serialize(summary, DurableAIJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<DurableSessionSummary>(json, DurableAIJsonUtilities.DefaultOptions);

        Assert.NotNull(back);
        Assert.Equal(summary.Text, back.Text);
        Assert.Equal(summary.ModelId, back.ModelId);
        Assert.Equal(12, back.Usage!.InputTokenCount);
        Assert.Equal(4, back.Usage.OutputTokenCount);
    }

    [Fact]
    public void Marker_OldWorkerSnapshot_ThrowsReplayCompatibilityException()
    {
        // Cypher mitigation #4: older worker (frozen baseline snapshot — only knows ai_request/ai_response)
        // pulls a workflow task whose history contains a compaction-marker entry. The harness
        // simulates that by filtering DurableSessionEntry's DerivedTypes down to the snapshot
        // set and asserting the typed exception fires with the marker discriminator named.
        var payload = JsonSerializer.Serialize<DurableSessionEntry>(
            NewMarker(), DurableAIJsonUtilities.DefaultOptions);

        var frozen = SourceGenCompatHarness.BuildFrozenContextSnapshot("v0_3");

        SourceGenCompatHarness.AssertReplayDeserialization(
            newOptions: DurableAIJsonUtilities.DefaultOptions,
            oldOptions: frozen,
            payload: payload,
            expectedDiscriminator: "compaction-marker");
    }

    [Fact]
    public void Marker_RequiredFields_AreEnforcedAtConstruction()
    {
        // The C# `required` modifier turns missing initializers into compile errors. We can't
        // exercise that at runtime, but we can verify that JsonSerializer surfaces a clear
        // failure when a payload lacks the required fields — proving the metadata IS marked
        // required at the type-info level.
        const string incomplete = """
            {
              "$type": "compaction-marker",
              "correlationId": "abc",
              "createdAt": "2026-05-14T00:00:00Z"
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DurableSessionEntry>(
                incomplete, DurableAIJsonUtilities.DefaultOptions));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static CompactionMarkerEntry NewMarker() => new()
    {
        CorrelationId = "marker-1",
        CreatedAt = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
        FirstReplacedEntryId = "entry-1",
        LastReplacedEntryId = "entry-3",
        ReplacedEntryCount = 3,
        FirstReplacedTurnId = "turn-1",
        LastReplacedTurnId = "turn-2",
        ReplacedTurnCount = 2,
        Strategy = "summarization",
        ModelId = "gpt-4o-mini",
        Summary = new DurableSessionSummary { Text = "summary text" },
    };
}
