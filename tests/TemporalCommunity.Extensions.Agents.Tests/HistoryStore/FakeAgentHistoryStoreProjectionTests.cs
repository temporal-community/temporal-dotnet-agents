#pragma warning disable TA002 // marker type is experimental but consumed by name in tests

using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Agents.State;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.HistoryStore;

/// <summary>
/// Step 5b tests: pins that the new <see cref="HistoryStore.IAgentHistoryStore.LoadAsync"/>
/// contract (<c>bool applyCompaction</c>) is honored by the reference test double.
/// </summary>
public class FakeAgentHistoryStoreProjectionTests
{
    [Fact]
    public async Task RawMode_ReturnsAllEntries_IncludingMarker()
    {
        // applyCompaction: false → audit canonical view. The marker and the source entries
        // it references are all returned untouched.
        var store = new FakeAgentHistoryStore();
        var entries = SeedMarkerAndSources(store, "session-raw");

        var loaded = await store.LoadAsync("session-raw", applyCompaction: false);

        Assert.Equal(entries.Count, loaded.Count);
        Assert.Contains(loaded, e => e is CompactionMarkerEntry);
        Assert.Contains(loaded, e => e.CorrelationId == "src-1");
        Assert.Contains(loaded, e => e.CorrelationId == "src-2");
    }

    [Fact]
    public async Task ProjectedMode_CollapsesReferencedSources_KeepsMarker()
    {
        // applyCompaction: true → the marker stays (it carries the rollup summary in
        // Messages), but the source entries it references drop out of the stream.
        var store = new FakeAgentHistoryStore();
        SeedMarkerAndSources(store, "session-projected");

        var loaded = await store.LoadAsync("session-projected", applyCompaction: true);

        Assert.Contains(loaded, e => e is CompactionMarkerEntry);
        Assert.DoesNotContain(loaded, e => e.CorrelationId == "src-1");
        Assert.DoesNotContain(loaded, e => e.CorrelationId == "src-2");
    }

    [Fact]
    public async Task ProjectedMode_NoMarkers_ReturnsRawUnchanged()
    {
        // When no markers are present, projection is a no-op — every entry passes through.
        var store = new FakeAgentHistoryStore();
        var sourceA = MakeSource("a");
        var sourceB = MakeSource("b");
        store.Seed("no-markers", new[] { sourceA, sourceB });

        var loaded = await store.LoadAsync("no-markers", applyCompaction: true);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("a", loaded[0].CorrelationId);
        Assert.Equal("b", loaded[1].CorrelationId);
    }

    [Fact]
    public async Task ProjectedMode_MarkerReferencesMissingEntry_ThrowsCompactionMarkerException()
    {
        // Cypher mitigation #3: an out-of-band erasure removed the source entry without
        // going through the erasure helper. The projection must NOT silently produce a
        // misleading reduced view — it must surface DurableCompactionMarkerException
        // with the missing IDs listed.
        var store = new FakeAgentHistoryStore();
        var marker = MakeMarker(
            id: "marker-orphan",
            compactedIds: new[] { "src-deleted-1", "src-deleted-2" });
        store.Seed("session-orphan", new DurableSessionEntry[] { marker });

        var ex = await Assert.ThrowsAsync<DurableCompactionMarkerException>(
            () => store.LoadAsync("session-orphan", applyCompaction: true));

        Assert.Equal("marker-orphan", ex.MarkerCorrelationId);
        Assert.Contains("src-deleted-1", ex.MissingMessageIds);
        Assert.Contains("src-deleted-2", ex.MissingMessageIds);
    }

    [Fact]
    public async Task RawMode_OrphanMarker_DoesNotThrow()
    {
        // Audit canonical view never validates marker references — it must always return
        // the raw store contents so compliance/erasure tooling can inspect them as-is.
        var store = new FakeAgentHistoryStore();
        store.Seed("session-raw-orphan", new DurableSessionEntry[]
        {
            MakeMarker(id: "marker-orphan", compactedIds: new[] { "src-deleted" }),
        });

        var loaded = await store.LoadAsync("session-raw-orphan", applyCompaction: false);

        Assert.Single(loaded);
        Assert.IsType<CompactionMarkerEntry>(loaded[0]);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static IReadOnlyList<DurableSessionEntry> SeedMarkerAndSources(
        FakeAgentHistoryStore store, string sessionId)
    {
        var source1 = MakeSource("src-1");
        var source2 = MakeSource("src-2");
        var marker = MakeMarker(
            id: "marker-1",
            compactedIds: new[] { "src-1", "src-2" });
        var fresh = MakeSource("src-3");

        var all = new DurableSessionEntry[] { source1, source2, marker, fresh };
        store.Seed(sessionId, all);
        return all;
    }

    private static AgentSessionRequest MakeSource(string id) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = Array.Empty<Microsoft.Extensions.AI.ChatMessage>(),
    };

    private static CompactionMarkerEntry MakeMarker(string id, string[] compactedIds) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        CompactedMessageIds = compactedIds,
        Strategy = "test-strategy",
        ModelId = string.Empty,
        OriginatingTurnCorrelationIds = new[] { "turn-1" },
    };
}
