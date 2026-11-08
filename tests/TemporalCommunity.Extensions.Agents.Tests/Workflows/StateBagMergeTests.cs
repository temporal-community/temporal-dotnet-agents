using System.Text.Json;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="StateBagMerge.Merge"/> — the deterministic, replay-safe merge of
/// untrusted tool/interceptor StateBag write-backs into the trusted carried bag.
///
/// Covers ordinary flat-key updates/deletions and deterministic index-order merging:
/// later index wins on a top-level key conflict, regardless of completion order.
/// </summary>
public class StateBagMergeTests
{
    /// <summary>Builds a <see cref="JsonElement"/> object from a property bag (deterministic test input).</summary>
    private static JsonElement? Bag(params (string Key, string Value)[] props)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in props)
            {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
        }
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static string? GetString(JsonElement? element, string key) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var prop)
            ? prop.GetString()
            : null;

    private static bool HasKey(JsonElement? element, string key) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out _);

    // ── Flat-key writes and index-order merge ────────────────────────────────

    [Fact]
    public void Merge_InterceptorWriteBack_OrdinaryKey_IsMerged()
    {
        var current = Bag(("existing", "value"));
        var writeBack = Bag(("interceptor.flag", "set"));

        var result = StateBagMerge.Merge(current, [writeBack]);

        Assert.Equal("value", GetString(result, "existing"));
        Assert.Equal("set", GetString(result, "interceptor.flag"));
    }

    [Fact]
    public void Merge_NoMergeableContent_ReturnsCurrentUnchanged()
    {
        var current = Bag(("k", "v"));

        var result = StateBagMerge.Merge(current, [Bag(), null]);

        Assert.Equal("v", GetString(result, "k"));
        Assert.False(HasKey(result, "other"));
    }

    // ── X-1: index-order merge, later index wins regardless of completion order ──

    [Fact]
    public void Merge_ConflictingKeys_LaterIndexWins()
    {
        var current = Bag(("base", "0"));
        // Two write-backs (tool index 0 and tool index 1) both write "shared".
        var index0 = Bag(("shared", "from-index-0"));
        var index1 = Bag(("shared", "from-index-1"));

        // updatedBags is ordered by tool-call index — later index (1) must win.
        var result = StateBagMerge.Merge(current, [index0, index1]);

        Assert.Equal("from-index-1", GetString(result, "shared"));
        Assert.Equal("0", GetString(result, "base"));
    }

    [Fact]
    public void Merge_ConflictingKeys_OrderIsDeterministic_NotCompletionDriven()
    {
        // The caller supplies write-backs in fixed tool-call index order. Passing the SAME
        // contributions in index order must always yield the later-index value — there is no
        // dependence on which activity "completed first".
        var current = Bag();
        var index0 = Bag(("key", "A"));
        var index1 = Bag(("key", "B"));
        var index2 = Bag(("key", "C"));

        var result = StateBagMerge.Merge(current, [index0, index1, index2]);

        Assert.Equal("C", GetString(result, "key"));
    }

    [Fact]
    public void Merge_NonConflictingKeys_AllContributionsMerged()
    {
        var current = Bag(("c", "0"));
        var result = StateBagMerge.Merge(current, [Bag(("a", "1")), Bag(("b", "2"))]);

        Assert.Equal("0", GetString(result, "c"));
        Assert.Equal("1", GetString(result, "a"));
        Assert.Equal("2", GetString(result, "b"));
    }

    [Fact]
    public void ApplyDelta_PropagatesKeyUpdatesAndDeletions()
    {
        var before = Bag(("keep", "value"), ("replace", "old"), ("remove", "gone"));
        var after = Bag(("keep", "value"), ("replace", "new"), ("add", "new"));

        var delta = StateBagMerge.CreateDelta(before, after);
        var result = StateBagMerge.ApplyDelta(before, delta);

        Assert.Equal("value", GetString(result, "keep"));
        Assert.Equal("new", GetString(result, "replace"));
        Assert.Equal("new", GetString(result, "add"));
        Assert.False(HasKey(result, "remove"));
    }

    [Fact]
    public void Merge_OutputKeysAreOrdinalSorted_ForReplayStability()
    {
        // The merge emits keys in ordinal order so the carried JsonElement is byte-stable.
        var result = StateBagMerge.Merge(
            Bag(("zebra", "1")),
            [Bag(("alpha", "2")), Bag(("mango", "3"))]);

        Assert.NotNull(result);
        var keys = result!.Value.EnumerateObject().Select(p => p.Name).ToList();
        var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, keys);
    }

    // ── OverlayTrustedStateBag — unfiltered trusted overlay (StateBag durability fix) ──────────
    //
    // Regression coverage for the cross-turn StateBag wipe: a turn ending on a hash-gated LLM step
    // returns a null/subset bag, and a plain REPLACE at the call site would wipe carried keys the
    // workflow thread wrote between activities. The overlay preserves carried keys the activity
    // did not touch, and applies ordinary flat-key overlay semantics.

    [Fact]
    public void OverlayTrustedStateBag_NullUpdated_ReturnsCurrentUnchanged()
    {
        // A hash-gated LLM step returns a null bag; carried values must survive.
        var current = Bag(("carried", "value"));

        var result = StateBagMerge.OverlayTrustedStateBag(current, updated: null);

        Assert.Equal("value", GetString(result, "carried"));
    }

    [Fact]
    public void OverlayTrustedStateBag_NullCurrent_ReturnsUpdated()
    {
        var updated = Bag(("app.context.file", "file.cs"));

        var result = StateBagMerge.OverlayTrustedStateBag(current: null, updated);

        Assert.Equal("file.cs", GetString(result, "app.context.file"));
    }

    [Fact]
    public void OverlayTrustedStateBag_KeyCollision_UpdatedWins()
    {
        var current = Bag(("shared", "old"));
        var updated = Bag(("shared", "new"));

        var result = StateBagMerge.OverlayTrustedStateBag(current, updated);

        Assert.Equal("new", GetString(result, "shared"));
    }

    [Fact]
    public void OverlayTrustedStateBag_PreservesCarriedKeysTheActivityDidNotTouch()
    {
        // The core durability guarantee: a subset bag from a context provider must not wipe
        // unrelated values already carried by the workflow.
        var current = Bag(("carried", "keep"), ("other", "value"));
        var updated = Bag(("app.context.file", "file.cs"));

        var result = StateBagMerge.OverlayTrustedStateBag(current, updated);

        Assert.Equal("keep", GetString(result, "carried"));
        Assert.Equal("value", GetString(result, "other"));
        Assert.Equal("file.cs", GetString(result, "app.context.file"));
    }

    [Fact]
    public void OverlayTrustedStateBag_EmitsKeysInOrdinalSortedOrder_ForReplayStableHash()
    {
        var current = Bag(("zebra", "1"), ("mango", "2"));
        var updated = Bag(("alpha", "3"));

        var result = StateBagMerge.OverlayTrustedStateBag(current, updated);

        Assert.NotNull(result);
        var keys = result!.Value.EnumerateObject().Select(p => p.Name).ToList();
        var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, keys);

        // Byte-stable: overlaying the same logical inputs must produce identical raw text (feeds
        // the FNV-1a content-hash gate; a divergence here would break replay determinism).
        var again = StateBagMerge.OverlayTrustedStateBag(
            Bag(("zebra", "1"), ("mango", "2")), Bag(("alpha", "3")));
        Assert.Equal(result!.Value.GetRawText(), again!.Value.GetRawText());
    }
}
