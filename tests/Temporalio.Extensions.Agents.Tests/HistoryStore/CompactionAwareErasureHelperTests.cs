#pragma warning disable TA002 // helper + marker are experimental but consumed by name in tests

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.State;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.HistoryStore;

/// <summary>
/// Step 5c tests: pin the erasure-cascade behavior matrix from Cypher mitigation #2.
/// </summary>
public class CompactionAwareErasureHelperTests
{
    [Fact]
    public async Task NoOverlap_MarkerPassesThrough_OnlyNonMarkersDropped()
    {
        // Erasure set targets non-marker entries that the marker does NOT reference.
        // Marker is unchanged; only the targeted source entries are removed.
        var store = new FakeAgentHistoryStore();
        var marker = MakeMarker("marker-1", compactedIds: new[] { "src-old-1", "src-old-2" });
        var oldA = MakeSource("src-old-1");
        var oldB = MakeSource("src-old-2");
        var freshC = MakeSource("src-fresh-c"); // unrelated, will be erased
        var freshD = MakeSource("src-fresh-d"); // unrelated, survives
        store.Seed("s1", new DurableSessionEntry[] { oldA, oldB, marker, freshC, freshD });

        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "s1", new HashSet<string> { "src-fresh-c" });

        Assert.Equal(0, result.MarkersAffected);
        Assert.Equal(0, result.MarkersTombstoned);
        Assert.Equal(0, result.MarkersRegenerated);

        var after = store.Snapshot("s1");
        Assert.DoesNotContain(after, e => e.CorrelationId == "src-fresh-c");
        Assert.Contains(after, e => e.CorrelationId == "src-fresh-d");
        Assert.Contains(after, e => e is CompactionMarkerEntry { CorrelationId: "marker-1" });
    }

    [Fact]
    public async Task PartialOverlap_RegeneratesMarker_WithSurvivingIdsAndClearedMessages()
    {
        // Two of the marker's three source IDs are in the erasure set. The marker is
        // regenerated with CompactedMessageIds reduced to the survivor and Messages cleared
        // (because the rollup summary may have been derived from the erased content).
        var store = new FakeAgentHistoryStore();
        var marker = WithSummary(
            MakeMarker("marker-2", compactedIds: new[] { "a", "b", "c" }),
            "rollup summary referencing all three sources");
        var entryA = MakeSource("a");
        var entryB = MakeSource("b");
        var entryC = MakeSource("c");
        store.Seed("s2", new DurableSessionEntry[] { entryA, entryB, entryC, marker });

        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "s2", new HashSet<string> { "a", "b" });

        Assert.Equal(1, result.MarkersAffected);
        Assert.Equal(0, result.MarkersTombstoned);
        Assert.Equal(1, result.MarkersRegenerated);

        var after = store.Snapshot("s2");
        var rewritten = after.OfType<CompactionMarkerEntry>().Single();
        Assert.Equal("marker-2", rewritten.CorrelationId);
        Assert.Equal(new[] { "c" }, rewritten.CompactedMessageIds);
        Assert.Empty(rewritten.Messages); // summary cleared
        // Erased non-marker entries are dropped too.
        Assert.DoesNotContain(after, e => e.CorrelationId == "a");
        Assert.DoesNotContain(after, e => e.CorrelationId == "b");
        Assert.Contains(after, e => e.CorrelationId == "c");
    }

    [Fact]
    public async Task FullOverlap_TombstonesMarker_DropsAllReferencedSources()
    {
        // Every source the marker referenced is in the erasure set. The marker is removed
        // entirely — nothing is left to summarize, and keeping the rollup summary would
        // leak content from the erased entries.
        var store = new FakeAgentHistoryStore();
        var marker = WithSummary(
            MakeMarker("marker-3", compactedIds: new[] { "x", "y" }),
            "summary that paraphrases x and y");
        var entryX = MakeSource("x");
        var entryY = MakeSource("y");
        var unrelated = MakeSource("z");
        store.Seed("s3", new DurableSessionEntry[] { entryX, entryY, marker, unrelated });

        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "s3", new HashSet<string> { "x", "y" });

        Assert.Equal(1, result.MarkersAffected);
        Assert.Equal(1, result.MarkersTombstoned);
        Assert.Equal(0, result.MarkersRegenerated);

        var after = store.Snapshot("s3");
        Assert.DoesNotContain(after, e => e is CompactionMarkerEntry);
        Assert.DoesNotContain(after, e => e.CorrelationId == "x");
        Assert.DoesNotContain(after, e => e.CorrelationId == "y");
        Assert.Contains(after, e => e.CorrelationId == "z");
    }

    [Fact]
    public async Task EmptyStore_ReturnsZeroResult_NoReplaceCall()
    {
        // No work to do, no ReplaceAsync dispatched — the helper short-circuits on empty load.
        var store = new FakeAgentHistoryStore();
        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "missing", new HashSet<string> { "irrelevant" });

        Assert.Equal(0, result.MarkersAffected);
        Assert.Equal(0, result.RemainingMessageCount);
        Assert.Equal(0, store.ReplaceCount);
    }

    [Fact]
    public async Task EmptyErasureSet_PreservesEverything()
    {
        // Calling with an empty erasure set is a no-op behaviorally — markers pass through,
        // sources pass through. The store IS rewritten (one ReplaceAsync) but with the same
        // contents.
        var store = new FakeAgentHistoryStore();
        var marker = MakeMarker("m", compactedIds: new[] { "a" });
        store.Seed("s4", new DurableSessionEntry[] { MakeSource("a"), marker });

        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "s4", new HashSet<string>());

        Assert.Equal(0, result.MarkersAffected);
        Assert.Equal(2, result.RemainingMessageCount);
    }

    [Fact]
    public async Task UsesAuditCanonicalLoad_Not_Projected()
    {
        // The helper MUST call LoadAsync(applyCompaction: false) — otherwise it would only
        // see the projected stream (markers minus referenced sources) and could not correctly
        // decide tombstone-vs-regenerate. Verify by inspecting the FakeAgentHistoryStore call
        // log indirectly: a projection-validation failure (orphan marker) does not abort the
        // helper because raw mode bypasses the validation.
        var store = new FakeAgentHistoryStore();
        // Orphan marker: references "ghost" which is NOT in the store.
        var orphan = MakeMarker("orphan", compactedIds: new[] { "ghost" });
        store.Seed("s5", new DurableSessionEntry[] { orphan });

        // If the helper used projected loads, this would throw DurableCompactionMarkerException
        // (per FakeAgentHistoryStore's projection-validation guard). Audit canonical mode
        // bypasses validation — helper proceeds and tombstones the orphan.
        var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
            store, "s5", new HashSet<string> { "ghost" });

        Assert.Equal(1, result.MarkersAffected);
        Assert.Equal(1, result.MarkersTombstoned);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static AgentSessionRequest MakeSource(string id) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = Array.Empty<ChatMessage>(),
    };

    private static CompactionMarkerEntry MakeMarker(string id, string[] compactedIds) => new()
    {
        CorrelationId = id,
        CreatedAt = DateTimeOffset.UtcNow,
        CompactedMessageIds = compactedIds,
        Strategy = "summarization",
        ModelId = "test-model",
        OriginatingTurnCorrelationIds = new[] { "turn-1" },
    };

    private static CompactionMarkerEntry WithSummary(CompactionMarkerEntry marker, string text) =>
        new()
        {
            CorrelationId = marker.CorrelationId,
            CreatedAt = marker.CreatedAt,
            Messages = new[] { new ChatMessage(ChatRole.Assistant, text) },
            CompactedMessageIds = marker.CompactedMessageIds,
            Strategy = marker.Strategy,
            ModelId = marker.ModelId,
            OriginatingTurnCorrelationIds = marker.OriginatingTurnCorrelationIds,
        };
}
