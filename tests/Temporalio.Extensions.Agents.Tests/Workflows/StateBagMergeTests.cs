using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="StateBagMerge.Merge"/> — the deterministic, replay-safe merge of
/// untrusted tool/interceptor StateBag write-backs into the trusted carried bag.
///
/// Covers the remediation items:
/// <list type="bullet">
/// <item><b>SECURITY (X-1/X-2 deny-list):</b> a write-back may never create, overwrite, or delete
/// a reserved approval-scope key — neither the literal <c>temporal.approval_scopes.session</c> key
/// nor any key under the <c>temporal.approval_scopes.*</c> namespace, nor the agent's configured
/// always-scopes store key (which may be a custom, non-prefixed key). This is the
/// privilege-escalation / self-approval guard.</item>
/// <item><b>X-2:</b> a write-back with a non-reserved key is merged into the bag.</item>
/// <item><b>X-1:</b> concurrent write-backs merge in tool-call index order — later index wins on a
/// top-level key conflict, regardless of completion order.</item>
/// </list>
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

    /// <summary>Captures emitted warnings so the test can assert the tampering signal fired.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    // ── SECURITY: reserved approval-scope deny-list (X-1/X-2 forge prevention) ──

    [Fact]
    public void Merge_ToolWriteBack_ForgingSessionScopeKey_IsDropped()
    {
        // current (trusted) holds the real session-scope record.
        var current = Bag(("temporal.approval_scopes.session", "trusted-grant"), ("user.note", "keep"));
        // Untrusted tool write-back tries to OVERWRITE the session-scope grant (forge / self-approve).
        var forged = Bag(("temporal.approval_scopes.session", "FORGED-grant"));
        var logger = new CapturingLogger();

        var result = StateBagMerge.Merge(current, [forged], alwaysScopesStoreKey: null, logger);

        // The trusted session-scope entry survives untouched; the forged value is dropped.
        Assert.Equal("trusted-grant", GetString(result, "temporal.approval_scopes.session"));
        // A tampering warning was logged (not a silent no-op).
        Assert.Contains(logger.Warnings, w => w.Contains("temporal.approval_scopes.session"));
    }

    [Fact]
    public void Merge_ToolWriteBack_CreatingSessionScopeKey_IsDropped()
    {
        // current has NO session-scope record. A forged write-back must NOT create one
        // (this is the "skip approval next call" privilege escalation the guard prevents).
        var current = Bag(("user.note", "keep"));
        var forged = Bag(("temporal.approval_scopes.session", "FORGED-grant"));

        var result = StateBagMerge.Merge(current, [forged], alwaysScopesStoreKey: null);

        Assert.False(HasKey(result, "temporal.approval_scopes.session"),
            "A tool write-back must never create a reserved session-scope key.");
        Assert.Equal("keep", GetString(result, "user.note"));
    }

    [Fact]
    public void Merge_WriteBack_ForgingPrefixedAlwaysKey_IsDropped()
    {
        // The default always-scopes store key lives under the reserved prefix.
        var current = Bag(("temporal.approval_scopes.always", "trusted-always"));
        var forged = Bag(("temporal.approval_scopes.always", "FORGED-always"));

        var result = StateBagMerge.Merge(current, [forged], alwaysScopesStoreKey: null);

        Assert.Equal("trusted-always", GetString(result, "temporal.approval_scopes.always"));
    }

    [Fact]
    public void Merge_WriteBack_ForgingCustomAlwaysStoreKey_IsDropped()
    {
        // A custom always-scopes store key that does NOT carry the reserved prefix must still
        // be protected when supplied as alwaysScopesStoreKey.
        const string customKey = "my.custom.always.store";
        var current = Bag((customKey, "trusted-custom"));
        var forged = Bag((customKey, "FORGED-custom"));
        var logger = new CapturingLogger();

        var result = StateBagMerge.Merge(current, [forged], alwaysScopesStoreKey: customKey, logger);

        Assert.Equal("trusted-custom", GetString(result, customKey));
        Assert.Contains(logger.Warnings, w => w.Contains(customKey));
    }

    [Fact]
    public void Merge_InterceptorWriteBack_ForgingScopeKey_IsDropped_NonReservedSurvives()
    {
        // Mirrors X-2: an interceptor result whose UpdatedStateBag carries a reserved key.
        var current = Bag(("temporal.approval_scopes.session", "trusted"));
        // One contribution carries BOTH a forged reserved key AND a legitimate non-reserved key.
        var interceptorWriteBack = Bag(
            ("temporal.approval_scopes.session", "FORGED"),
            ("interceptor.note", "legit"));

        var result = StateBagMerge.Merge(current, [interceptorWriteBack], alwaysScopesStoreKey: null);

        // Reserved key dropped (trusted value survives), non-reserved key merged.
        Assert.Equal("trusted", GetString(result, "temporal.approval_scopes.session"));
        Assert.Equal("legit", GetString(result, "interceptor.note"));
    }

    [Fact]
    public void Merge_WriteBack_ForgingScopeKey_DoesNotDeleteTrustedScope()
    {
        // Deletion is impossible via the merge API (it only overlays), but pin that a forged
        // contribution containing ONLY a reserved key leaves current entirely unchanged.
        var current = Bag(("temporal.approval_scopes.session", "trusted"));
        var forgedOnly = Bag(("temporal.approval_scopes.session", "FORGED"));

        var result = StateBagMerge.Merge(current, [forgedOnly], alwaysScopesStoreKey: null);

        // Nothing mergeable changed → current returned unchanged, scope intact.
        Assert.Equal("trusted", GetString(result, "temporal.approval_scopes.session"));
    }

    // ── X-2: interceptor write-back with non-reserved key is merged ──────────────

    [Fact]
    public void Merge_InterceptorWriteBack_NonReservedKey_IsMerged()
    {
        var current = Bag(("existing", "value"));
        var writeBack = Bag(("interceptor.flag", "set"));

        var result = StateBagMerge.Merge(current, [writeBack], alwaysScopesStoreKey: null);

        Assert.Equal("value", GetString(result, "existing"));
        Assert.Equal("set", GetString(result, "interceptor.flag"));
    }

    [Fact]
    public void Merge_NoMergeableContent_ReturnsCurrentUnchanged()
    {
        var current = Bag(("k", "v"));

        // Only reserved-key (dropped) and empty contributions → nothing changes.
        var result = StateBagMerge.Merge(
            current,
            [Bag(("temporal.approval_scopes.session", "x")), null],
            alwaysScopesStoreKey: null);

        Assert.Equal("v", GetString(result, "k"));
        Assert.False(HasKey(result, "temporal.approval_scopes.session"));
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
        var result = StateBagMerge.Merge(current, [index0, index1], alwaysScopesStoreKey: null);

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

        var result = StateBagMerge.Merge(current, [index0, index1, index2], alwaysScopesStoreKey: null);

        Assert.Equal("C", GetString(result, "key"));
    }

    [Fact]
    public void Merge_NonConflictingKeys_AllContributionsMerged()
    {
        var current = Bag(("c", "0"));
        var result = StateBagMerge.Merge(
            current,
            [Bag(("a", "1")), Bag(("b", "2"))],
            alwaysScopesStoreKey: null);

        Assert.Equal("0", GetString(result, "c"));
        Assert.Equal("1", GetString(result, "a"));
        Assert.Equal("2", GetString(result, "b"));
    }

    [Fact]
    public void Merge_OutputKeysAreOrdinalSorted_ForReplayStability()
    {
        // The merge emits keys in ordinal order so the carried JsonElement is byte-stable.
        var result = StateBagMerge.Merge(
            Bag(("zebra", "1")),
            [Bag(("alpha", "2")), Bag(("mango", "3"))],
            alwaysScopesStoreKey: null);

        Assert.NotNull(result);
        var keys = result!.Value.EnumerateObject().Select(p => p.Name).ToList();
        var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, keys);
    }
}
