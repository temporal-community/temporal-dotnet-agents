using System.Text.Json;
using TemporalCommunity.Extensions.Agents.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Option D Phase 1c — the session, not the agent, owns the StateBag. These tests pin the two
/// update operations the durable-agent loop performs against it: a trusted overlay after each
/// LLM step, and an index-ordered merge of untrusted tool/interceptor write-backs after the
/// complete fan-out.
/// </summary>
public class TemporalAgentSessionStateBagTests
{
    private static TemporalAgentSession NewSession(string key = "abc123") =>
        new(new TemporalAgentSessionId("Assistant", key));

    private static JsonElement Bag(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return JsonSerializer.SerializeToElement(dict);
    }

    private static string? Read(TemporalAgentSession session, string key) =>
        session.StateBag.TryGetValue<string>(key, out var value) ? value : null;

    [Fact]
    public void SerializeStateBag_EmptyBag_ReturnsNull()
    {
        Assert.Null(NewSession().SerializeStateBag());
    }

    [Fact]
    public void OverlayTrustedStateBag_MergesPerKey_PreservingUntouchedKeys()
    {
        var session = NewSession();
        session.StateBag.SetValue("carried", "keep-me");
        session.StateBag.SetValue("shared", "old");

        session.OverlayTrustedStateBag(Bag(("shared", "new"), ("added", "fresh")));

        Assert.Equal("keep-me", Read(session, "carried"));
        Assert.Equal("new", Read(session, "shared"));
        Assert.Equal("fresh", Read(session, "added"));
    }

    // The regression this guards: an LLM step that is hash-gated (or otherwise produces no bag)
    // returns null. Replacing rather than overlaying would wipe everything written before it.
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void OverlayTrustedStateBag_NoActivityBag_LeavesSessionBagIntact(string? json)
    {
        var session = NewSession();
        session.StateBag.SetValue("carried", "keep-me");

        JsonElement? updated = json is null ? null : JsonDocument.Parse(json).RootElement;
        session.OverlayTrustedStateBag(updated);

        Assert.Equal("keep-me", Read(session, "carried"));
        Assert.Equal(1, session.StateBag.Count);
    }

    [Fact]
    public void OverlayTrustedStateBag_OntoEmptyBag_AdoptsActivityBag()
    {
        var session = NewSession();
        session.OverlayTrustedStateBag(Bag(("app.context.file", "src/a.cs")));
        Assert.Equal("src/a.cs", Read(session, "app.context.file"));
    }

    // Determinism: tool activities fan out concurrently, so completion order varies between the
    // original run and replay. The merge must depend only on the tool-call index the caller
    // supplies — later index wins — never on which activity finished first.
    [Fact]
    public void MergeToolStateBagWriteBacks_LaterIndexWinsRegardlessOfCompletionOrder()
    {
        var first = Bag(("contested", "from-index-0"), ("only-0", "a"));
        var second = Bag(("contested", "from-index-1"), ("only-1", "b"));

        var session = NewSession();
        session.MergeToolStateBagWriteBacks([first, second]);

        Assert.Equal("from-index-1", Read(session, "contested"));
        Assert.Equal("a", Read(session, "only-0"));
        Assert.Equal("b", Read(session, "only-1"));

        // Same contributions, opposite array order => the other index is last, so it wins.
        // This proves the outcome is a function of index order, not of arrival.
        var reversed = NewSession();
        reversed.MergeToolStateBagWriteBacks([second, first]);
        Assert.Equal("from-index-0", Read(reversed, "contested"));
    }

    [Fact]
    public void MergeToolStateBagWriteBacks_SkippedToolsContributeNothing()
    {
        var session = NewSession();
        session.StateBag.SetValue("carried", "keep-me");

        // A blocked / skipped / synthetic-result tool leaves a null slot at its index.
        session.MergeToolStateBagWriteBacks([null, Bag(("written", "yes")), null]);

        Assert.Equal("keep-me", Read(session, "carried"));
        Assert.Equal("yes", Read(session, "written"));
    }

    [Fact]
    public void MergeToolStateBagWriteBacks_NoContributions_LeavesBagUntouched()
    {
        var session = NewSession();
        session.StateBag.SetValue("carried", "keep-me");

        session.MergeToolStateBagWriteBacks([null, null]);

        Assert.Equal(1, session.StateBag.Count);
        Assert.Equal("keep-me", Read(session, "carried"));
    }

    // SECURITY: approval-scope records are written only by the trusted workflow thread. A tool
    // write-back that names a reserved key must be dropped, not merged — otherwise a tool could
    // forge its own approval grant.
    [Fact]
    public void MergeToolStateBagWriteBacks_DropsReservedApprovalScopeKeys()
    {
        var session = NewSession();
        session.StateBag.SetValue("temporal.approval_scopes.session", "trusted-grant");

        session.MergeToolStateBagWriteBacks(
        [
            Bag(("temporal.approval_scopes.session", "forged"),
                ("temporal.approval_scopes.always", "forged-too"),
                ("legit", "ok")),
        ]);

        Assert.Equal("trusted-grant", Read(session, "temporal.approval_scopes.session"));
        Assert.Null(Read(session, "temporal.approval_scopes.always"));
        Assert.Equal("ok", Read(session, "legit"));
    }

    [Fact]
    public void MergeToolStateBagWriteBacks_DropsCustomAlwaysScopeStoreKey()
    {
        var session = NewSession();
        session.StateBag.SetValue("my.custom.scopes", "trusted-grant");

        session.MergeToolStateBagWriteBacks(
            [Bag(("my.custom.scopes", "forged"))],
            alwaysScopesStoreKey: "my.custom.scopes");

        Assert.Equal("trusted-grant", Read(session, "my.custom.scopes"));
    }

    [Fact]
    public void MergeToolStateBagWriteBacks_NullWriteBacks_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewSession().MergeToolStateBagWriteBacks(null!));
    }

    // Two sessions on one agent must not see each other's StateBag. Before Option D the bag lived
    // on the agent instance, so session B's writes overwrote session A's.
    [Fact]
    public void StateBag_IsIsolatedBetweenSessions()
    {
        var a = NewSession("aaa");
        var b = NewSession("bbb");

        a.OverlayTrustedStateBag(Bag(("owner", "a")));
        b.OverlayTrustedStateBag(Bag(("owner", "b")));

        Assert.Equal("a", Read(a, "owner"));
        Assert.Equal("b", Read(b, "owner"));
    }

    [Fact]
    public void StateBag_SurvivesSerializationRoundTrip()
    {
        var session = NewSession();
        session.OverlayTrustedStateBag(Bag(("app.context.file", "src/a.cs")));
        session.MergeToolStateBagWriteBacks([Bag(("tool.note", "written"))]);

        var restored = TemporalAgentSession.Deserialize(session.Serialize());

        Assert.Equal("src/a.cs", Read(restored, "app.context.file"));
        Assert.Equal("written", Read(restored, "tool.note"));
    }

    // The reserved-key deny-list compares Ordinal, so a case variant is NOT dropped — it is merged
    // as a distinct key. That is safe only because the read side is Ordinal too: readers look up
    // the literal reserved key, and AgentSessionStateBag is backed by an ordinal-comparer
    // dictionary. This test pins that symmetry. If the bag ever moves to OrdinalIgnoreCase, a tool
    // could forge a grant by writing "TEMPORAL.APPROVAL_SCOPES.SESSION" — and this test is what
    // catches it.
    [Theory]
    [InlineData("TEMPORAL.APPROVAL_SCOPES.SESSION")]
    [InlineData("Temporal.Approval_Scopes.Session")]
    public void MergeToolStateBagWriteBacks_CaseVariantOfReservedKey_LandsInert(string variantKey)
    {
        const string reserved = "temporal.approval_scopes.session";

        var session = NewSession();
        session.StateBag.SetValue(reserved, "trusted-grant");

        session.MergeToolStateBagWriteBacks([Bag((variantKey, "forged"))]);

        // The genuine reserved key — the one readers actually look up — is untouched.
        Assert.Equal("trusted-grant", Read(session, reserved));

        // The variant landed, but as a separate key that no reader consults.
        Assert.Equal("forged", Read(session, variantKey));
        Assert.NotEqual(reserved, variantKey);
    }

    [Fact]
    public void MergeToolStateBagWriteBacks_UnicodeEscapedReservedKey_IsStillDropped()
    {
        // JSON \u escapes are decoded before the deny-list sees the name, so escaping the key in
        // the payload does not smuggle it past the Ordinal prefix check.
        const string reserved = "temporal.approval_scopes.session";
        var escaped = JsonDocument.Parse(
            """{"\u0074emporal.approval_scopes.session":"forged"}""").RootElement;

        var session = NewSession();
        session.StateBag.SetValue(reserved, "trusted-grant");

        session.MergeToolStateBagWriteBacks([escaped]);

        Assert.Equal("trusted-grant", Read(session, reserved));
        Assert.Equal(1, session.StateBag.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void MergeToolStateBagWriteBacks_NonObjectWriteBack_IsIgnored(string json)
    {
        var session = NewSession();
        session.StateBag.SetValue("carried", "keep-me");

        session.MergeToolStateBagWriteBacks([JsonDocument.Parse(json).RootElement]);

        Assert.Equal(1, session.StateBag.Count);
        Assert.Equal("keep-me", Read(session, "carried"));
    }
}
