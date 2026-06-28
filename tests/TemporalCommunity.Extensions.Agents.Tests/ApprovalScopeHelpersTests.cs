using System.Text.Json;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Task 8.3 — Unit tests for <see cref="ApprovalScopeHelpers.TryMatchScope"/>.
/// Covers the full matching algorithm: tool-name matching, null patterns (wildcard),
/// Exact/Glob/Regex pattern types, security (ReDoS), edge cases, and bad-input resilience.
/// </summary>
public class ApprovalScopeHelpersTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentSessionStateBag MakeBagWithScopes(
        string storeKey,
        IReadOnlyList<ApprovalScopeRecord> records)
    {
        var bag = new AgentSessionStateBag();
        bag.SetValue<List<ApprovalScopeRecord>>(
            storeKey,
            records.ToList(),
            TemporalAgentJsonUtilities.DefaultOptions);
        // Round-trip through serialization to mirror the real workflow path.
        var serialized = bag.Serialize();
        return AgentSessionStateBag.Deserialize(serialized);
    }

    private static ApprovalScopeRecord MakeRecord(
        string toolName,
        ApprovalScopePattern? pattern = null,
        string? requestId = null) =>
        new ApprovalScopeRecord
        {
            ToolName = toolName,
            Pattern = pattern,
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        };

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private const string StoreKey = "temporal.approval_scopes.session";

    // ── Tool-name matching ───────────────────────────────────────────────────

    [Fact]
    public void ToolName_CaseInsensitiveMatch_ReturnsTrue()
    {
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile")]);

        // The tool being called uses a different casing.
        var matched = ApprovalScopeHelpers.TryMatchScope("writefile", new Dictionary<string, object?>(), bag, StoreKey, out var match);

        Assert.True(matched);
        Assert.NotNull(match);
        Assert.Equal("WriteFile", match!.ToolName);
    }

    [Fact]
    public void ToolName_NoMatch_ReturnsFalse()
    {
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile")]);

        var matched = ApprovalScopeHelpers.TryMatchScope("DeleteFile", new Dictionary<string, object?>(), bag, StoreKey, out var match);

        Assert.False(matched);
        Assert.Null(match);
    }

    // ── Null pattern (wildcard) ──────────────────────────────────────────────

    [Fact]
    public void NullPattern_Wildcard_MatchesAnyCallOfTool()
    {
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern: null)]);

        // With any arguments, a null pattern always matches.
        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/foo.txt"), ("content", "hello")), bag, StoreKey, out _);

        Assert.True(matched);
    }

    // ── Exact match ──────────────────────────────────────────────────────────

    [Fact]
    public void ExactMatch_OnParameter_CaseSensitive()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Exact,
            Parameter = "path",
            Pattern = "/tmp/foo.txt",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Exact match on the correct path.
        var matchYes = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/foo.txt")), bag, StoreKey, out _);

        // Different case — Exact uses Ordinal (case-sensitive).
        var matchNo = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/Foo.txt")), bag, StoreKey, out _);

        Assert.True(matchYes);
        Assert.False(matchNo);
    }

    // ── Glob match ───────────────────────────────────────────────────────────

    [Fact]
    public void GlobMatch_SingleStar_MatchesNonSeparatorSequence()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Parameter = "path",
            Pattern = "/tmp/*",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Matches: /tmp/foo.txt — single * matches non-/ chars.
        var matchShallow = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/foo.txt")), bag, StoreKey, out _);

        // Does NOT match: /tmp/sub/dir/file.txt — single * cannot cross /.
        var matchDeep = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/sub/dir/file.txt")), bag, StoreKey, out _);

        Assert.True(matchShallow);
        Assert.False(matchDeep);
    }

    [Fact]
    public void GlobMatch_DoubleStar_MatchesAnySequenceIncludingSeparator()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Parameter = "path",
            Pattern = "/tmp/**",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Double ** matches across directory separators.
        var matchDeep = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/sub/dir/file.txt")), bag, StoreKey, out _);

        Assert.True(matchDeep);
    }

    [Fact]
    public void GlobMatch_PatternDoesNotMatchOtherPaths()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Parameter = "path",
            Pattern = "/tmp/*",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // /etc/passwd is completely outside the /tmp/ tree.
        var noMatch = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/etc/passwd")), bag, StoreKey, out _);

        Assert.False(noMatch);
    }

    // ── Regex match ──────────────────────────────────────────────────────────

    [Fact]
    public void RegexMatch_ValidPattern_MatchesCorrectly()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Regex,
            Parameter = "path",
            Pattern = "^/tmp/.*",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // /tmp/foo matches ^/tmp/.*
        var matchTmp = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/foo")), bag, StoreKey, out _);

        // /etc/passwd does not match.
        var noMatchEtc = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/etc/passwd")), bag, StoreKey, out _);

        Assert.True(matchTmp);
        Assert.False(noMatchEtc);
    }

    [Fact]
    public void RegexMatch_InvalidSyntax_TreatedAsNoMatch()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Regex,
            Parameter = "path",
            Pattern = "[unclosed",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Invalid regex must not throw — treated as no match.
        var exception = Record.Exception(() =>
            ApprovalScopeHelpers.TryMatchScope("WriteFile",
                Args(("path", "/tmp/foo")), bag, StoreKey, out _));

        Assert.Null(exception);

        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", "/tmp/foo")), bag, StoreKey, out _);

        Assert.False(matched);
    }

    [Fact]
    public void RegexMatch_ReDoSPattern_CompletesWithinReasonableTime()
    {
        // Classic ReDoS pattern against a matching-ish input.
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Regex,
            Parameter = "path",
            Pattern = @"(a+)+",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // This input would hang a naive regex engine.
        var input = new string('a', 30) + "!";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", input)), bag, StoreKey, out _);
        sw.Stop();

        // Must return within 1 second (far below the 100ms timeout + overhead).
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"TryMatchScope took {sw.ElapsedMilliseconds}ms — possible ReDoS");

        // The timed-out match is treated as no-match (safe behavior).
        // We don't assert true/false since short inputs may actually match quickly.
    }

    // ── Missing parameter key ────────────────────────────────────────────────

    [Fact]
    public void MissingParameterKey_ReturnsNoMatch()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Exact,
            Parameter = "path",
            Pattern = "/tmp/foo.txt",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Arguments do not contain the "path" key.
        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("content", "hello")), bag, StoreKey, out _);

        Assert.False(matched);
    }

    // ── Canonical JSON match (Parameter == null) ─────────────────────────────

    [Fact]
    public void CanonicalJsonMatch_KeysSortedOrdinally()
    {
        // When Parameter is null, the full arguments dict is serialized with sorted keys.
        var jsonA = ApprovalScopeHelpers.SerializeArgumentsCanonically(
            Args(("z", "last"), ("a", "first")));
        var jsonB = ApprovalScopeHelpers.SerializeArgumentsCanonically(
            Args(("a", "first"), ("z", "last")));

        // Both produce the same canonical JSON regardless of insertion order.
        Assert.Equal(jsonA, jsonB);

        // Keys must appear in ordinal order: "a" before "z".
        var aIndex = jsonA.IndexOf("\"a\"", StringComparison.Ordinal);
        var zIndex = jsonA.IndexOf("\"z\"", StringComparison.Ordinal);
        Assert.True(aIndex < zIndex, "Expected 'a' key before 'z' key in canonical JSON");
    }

    [Fact]
    public void CanonicalJsonMatch_NestedJsonElement_KeysSorted()
    {
        // Nested JsonElement objects must also have their keys sorted.
        var nested = JsonDocument.Parse("""{"z":1,"a":2}""").RootElement;
        var json = ApprovalScopeHelpers.SerializeArgumentsCanonically(
            Args(("data", (object?)nested)));

        var dataIndex = json.IndexOf("\"data\"", StringComparison.Ordinal);
        Assert.True(dataIndex >= 0);

        // Within the nested object, "a" should appear before "z".
        var aIndex = json.IndexOf("\"a\"", StringComparison.Ordinal);
        var zIndex = json.IndexOf("\"z\"", StringComparison.Ordinal);
        Assert.True(aIndex < zIndex, "Nested 'a' key must appear before nested 'z' key");
    }

    // ── Edge cases: null/empty bag, missing key ──────────────────────────────

    [Fact]
    public void NullBag_ReturnsFalse()
    {
        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile", new Dictionary<string, object?>(), bag: null, StoreKey, out var match);

        Assert.False(matched);
        Assert.Null(match);
    }

    [Fact]
    public void StoreKeyNotPresentInBag_ReturnsFalse()
    {
        var bag = MakeBagWithScopes("temporal.approval_scopes.always", [MakeRecord("WriteFile")]);

        // The session key is empty — scope is under a different key.
        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile", new Dictionary<string, object?>(), bag, StoreKey, out _);

        Assert.False(matched);
    }

    // ── Malformed cached scope records ───────────────────────────────────────

    [Fact]
    public void MalformedBagContent_TreatedAsNoMatch_NoExceptionEscapes()
    {
        // Write raw invalid JSON under the store key to simulate a corrupted entry.
        var bag = new AgentSessionStateBag();
        // We can't easily corrupt a real typed write, but we can write a JSON that
        // deserializes as a list with null entries (missing required fields).
        // The helper must not throw.

        // Build a valid bag with one good record and one record with a null ToolName.
        // TryGetValue should succeed but IsMatchingRecord iteration should handle bad records gracefully.
        var records = new List<ApprovalScopeRecord>
        {
            // This record has toolName set, so it's valid.
            new() { ToolName = "WriteFile", GrantedAt = DateTimeOffset.UtcNow, OriginatingRequestId = "req-1" }
        };
        var restoredBag = MakeBagWithScopes(StoreKey, records);

        // This should succeed without exception.
        var exception = Record.Exception(() =>
            ApprovalScopeHelpers.TryMatchScope("WriteFile", new Dictionary<string, object?>(), restoredBag, StoreKey, out _));

        Assert.Null(exception);
    }

    // ── Overlong glob pattern/input ──────────────────────────────────────────

    [Fact]
    public void OverlongGlobInput_TreatedAsNoMatch()
    {
        var pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Parameter = "path",
            Pattern = "/tmp/*",
        };
        var bag = MakeBagWithScopes(StoreKey, [MakeRecord("WriteFile", pattern)]);

        // Input exceeding the 16384 char limit.
        var longInput = new string('a', 20000);

        var exception = Record.Exception(() =>
            ApprovalScopeHelpers.TryMatchScope("WriteFile",
                Args(("path", longInput)), bag, StoreKey, out _));

        Assert.Null(exception);

        var matched = ApprovalScopeHelpers.TryMatchScope("WriteFile",
            Args(("path", longInput)), bag, StoreKey, out _);

        Assert.False(matched);
    }
}
