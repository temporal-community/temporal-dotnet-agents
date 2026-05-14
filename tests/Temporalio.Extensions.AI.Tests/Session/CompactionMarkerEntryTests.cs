#pragma warning disable TA002 // marker type is experimental but referenced by-name in tests

using System.Text.Json;
using Temporalio.Extensions.AI.Tests.Compat;
using Xunit;

namespace Temporalio.Extensions.AI.Tests.Session;

/// <summary>
/// Step 5a tests: marker type wire format, discriminator pinning, and backward-compat
/// behavior under the Step-2 source-gen harness.
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
        Assert.Equal(original.CompactedMessageIds, marker.CompactedMessageIds);
        Assert.Equal(original.Strategy, marker.Strategy);
        Assert.Equal(original.ModelId, marker.ModelId);
        Assert.Equal(original.OriginatingTurnCorrelationIds, marker.OriginatingTurnCorrelationIds);
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
    public void Marker_OldWorkerSnapshot_ThrowsReplayCompatibilityException()
    {
        // Cypher mitigation #4: older worker (v0.3 snapshot — only knows ai_request/ai_response)
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
        CompactedMessageIds = new[] { "msg-1", "msg-2", "msg-3" },
        Strategy = "summarization",
        ModelId = "gpt-4o-mini",
        OriginatingTurnCorrelationIds = new[] { "turn-a", "turn-b" },
    };
}
