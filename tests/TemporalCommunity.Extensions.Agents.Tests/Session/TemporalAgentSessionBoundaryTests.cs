using System.Text.Json;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Option D Gate 5 (structural subcase) — round-trips a session through the public MAF
/// serialization boundary (<c>SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>) and
/// asserts that both the typed StateBag and the session-owned history survive.
/// </summary>
/// <remarks>
/// The end-to-end subcase (history accumulated by an actual agent run) belongs to Phase 1d and
/// lives in the integration suite, because <see cref="TemporalAIAgent"/> can only run inside a
/// Temporal workflow. Serialization itself touches no workflow API, so it is exercised here.
/// </remarks>
public class TemporalAgentSessionBoundaryTests
{
    private static TemporalAIAgent CreateAgent() => new("Assistant");

    private static AgentSessionRequest Request(string correlationId, string text) => new()
    {
        CorrelationId = correlationId,
        CreatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
        Messages = [new ChatMessage(ChatRole.User, text)],
    };

    private static AgentSessionResponse Response(string correlationId, string text) => new()
    {
        CorrelationId = correlationId,
        CreatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 1, TimeSpan.Zero),
        Messages = [new ChatMessage(ChatRole.Assistant, text)],
    };

    [Fact]
    public async Task Gate5_RoundTrip_PreservesStateBagAndHistoryInOrder()
    {
        var agent = CreateAgent();
        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.StateBag.SetValue("temporal.working_set", "src/a.cs");
        session.AppendHistoryEntry(Request("c1", "first"));
        session.AppendHistoryEntry(Response("c1", "first reply"));
        session.AppendHistoryEntry(Request("c2", "second"));

        var serialized = await agent.SerializeSessionAsync(session);
        var restored = Assert.IsType<TemporalAgentSession>(await agent.DeserializeSessionAsync(serialized));

        Assert.Equal(session.SessionId, restored.SessionId);
        Assert.Equal("src/a.cs", restored.StateBag.GetValue<string>("temporal.working_set"));

        Assert.Equal(3, restored.History.Count);
        Assert.IsType<AgentSessionRequest>(restored.History[0]);
        Assert.IsType<AgentSessionResponse>(restored.History[1]);
        Assert.IsType<AgentSessionRequest>(restored.History[2]);
        Assert.Equal(["first", "first reply", "second"], restored.History.Select(e => e.Messages[0].Text));
        Assert.Equal(["c1", "c1", "c2"], restored.History.Select(e => e.CorrelationId));
    }

    [Fact]
    public async Task Gate5_RoundTrip_UsesGeneratedResolver_NotReflection()
    {
        // The whole point of routing through the snapshot DTO: before this change the boundary
        // serialized TemporalAgentSession directly and silently resolved through
        // DefaultJsonTypeInfoResolver. Assert the registered root is what gets used.
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions
            .GetTypeInfo(typeof(TemporalAgentSessionSnapshot));
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);

        var agent = CreateAgent();
        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        var serialized = await agent.SerializeSessionAsync(session);

        // Empty session: only sessionId is written.
        Assert.Single(serialized.EnumerateObject());
        Assert.Equal("ta-assistant-abc123", serialized.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Gate5_LegacySnapshotWithoutHistory_RestoresWithEmptyHistory()
    {
        // Byte-for-byte the shape the pre-change direct-session serializer emitted.
        var legacy = JsonDocument.Parse(
            """{"sessionId":"ta-assistant-abc123","stateBag":{"temporal.working_set":"src/a.cs"}}""")
            .RootElement;

        var agent = CreateAgent();
        var restored = Assert.IsType<TemporalAgentSession>(await agent.DeserializeSessionAsync(legacy));

        Assert.Equal("ta-assistant-abc123", restored.SessionId.WorkflowId);
        Assert.Equal("src/a.cs", restored.StateBag.GetValue<string>("temporal.working_set"));
        Assert.Empty(restored.History);
    }

    [Fact]
    public async Task Gate5_LegacySnapshotWithoutStateBag_RestoresWithEmptyBag()
    {
        var legacy = JsonDocument.Parse("""{"sessionId":"ta-assistant-abc123"}""").RootElement;

        var agent = CreateAgent();
        var restored = Assert.IsType<TemporalAgentSession>(await agent.DeserializeSessionAsync(legacy));

        Assert.Equal(0, restored.StateBag.Count);
        Assert.Empty(restored.History);
    }

    [Fact]
    public async Task Gate5_MissingSessionId_Throws()
    {
        var agent = CreateAgent();
        await Assert.ThrowsAsync<JsonException>(async () =>
            await agent.DeserializeSessionAsync(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public async Task Gate5_ProxyAndWorkflowAgent_ShareTheSameWireShape()
    {
        // Both agent types delegate to the same session helpers. If they ever diverge, a session
        // serialized by one could not be restored by the other.
        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.StateBag.SetValue("k", "v");
        session.AppendHistoryEntry(Request("c1", "hello"));

        var workflowAgent = CreateAgent();
        var proxy = new TemporalAIAgentProxy("Assistant", A.Fake<ITemporalAgentClient>());

        var fromWorkflowAgent = await workflowAgent.SerializeSessionAsync(session);
        var fromProxy = await proxy.SerializeSessionAsync(session);

        Assert.Equal(fromWorkflowAgent.GetRawText(), fromProxy.GetRawText());

        var restoredByProxy = Assert.IsType<TemporalAgentSession>(
            await proxy.DeserializeSessionAsync(fromWorkflowAgent));
        Assert.Single(restoredByProxy.History);
    }

    [Fact]
    public void History_IsNotCastableToMutableList()
    {
        // The facade must not leak the backing store — an internal caller casting back to List<T>
        // could append entries without going through AppendHistoryEntry.
        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        Assert.IsNotType<List<DurableSessionEntry>>(session.History);
    }

    // ─── Legacy wire compatibility ──────────────────────────────────────────
    //
    // The fixtures below are the shapes the pre-Option-D serializer actually emitted, captured by
    // running the real v0.3 `TemporalAgentSession.Serialize()` against a built assembly — not
    // hand-guessed. A break here means sessions already persisted in workflow histories can no
    // longer be restored.

    [Fact]
    public async Task Legacy_EmptyStateBagObject_RestoresAsEmptyBag()
    {
        // The v0.3 writer always emitted the property, so an empty bag serialized as "stateBag":{}.
        // The snapshot writer omits it instead; the reader must still accept the old form.
        var legacy = JsonDocument.Parse("""{"sessionId":"ta-assistant-abc123","stateBag":{}}""").RootElement;

        var restored = Assert.IsType<TemporalAgentSession>(
            await CreateAgent().DeserializeSessionAsync(legacy));

        Assert.Equal(0, restored.StateBag.Count);
        Assert.Empty(restored.History);
    }

    [Fact]
    public async Task Current_PopulatedSession_EmitsTheLegacyPropertyNames()
    {
        // A single StateBag key on purpose: AgentSessionStateBag is backed by a ConcurrentDictionary,
        // so multi-key serialization order is hash order and not stable enough to assert on.
        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.StateBag.SetValue("k", "v");

        var serialized = await CreateAgent().SerializeSessionAsync(session);

        Assert.Equal("ta-assistant-abc123", serialized.GetProperty("sessionId").GetString());
        Assert.Equal("v", serialized.GetProperty("stateBag").GetProperty("k").GetString());
        Assert.False(serialized.TryGetProperty("history", out _));
    }

    // ─── Caller-supplied JsonSerializerOptions ──────────────────────────────

    [Fact]
    public async Task ForeignOptions_StillRoundTripAgentHistoryEntrySubtypes()
    {
        // MAF forwards whatever options the caller passed. The agent_request / agent_response
        // discriminators come only from TemporalAgentJsonUtilities' runtime resolver modifier.
        // Without the fix this throws NotSupportedException ("Runtime type AgentSessionRequest is
        // not supported by polymorphic type DurableSessionEntry") — i.e. every turn that persists a
        // session with history fails outright. Verified by reverting the fix.
        var foreignOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Reflection-only: resolves DurableSessionEntry, but only with the base-class
            // discriminators. Exactly the configuration that breaks history serialization.
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        foreignOptions.MakeReadOnly();

        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.AppendHistoryEntry(Request("c1", "hello"));
        session.AppendHistoryEntry(Response("c1", "hi back"));

        var agent = CreateAgent();
        var serialized = await agent.SerializeSessionAsync(session, foreignOptions);
        var restored = Assert.IsType<TemporalAgentSession>(
            await agent.DeserializeSessionAsync(serialized, foreignOptions));

        Assert.Equal(2, restored.History.Count);
        Assert.IsType<AgentSessionRequest>(restored.History[0]);
        Assert.IsType<AgentSessionResponse>(restored.History[1]);
        Assert.Equal("hello", restored.History[0].Messages[0].Text);
    }

    [Fact]
    public async Task DerivedOptionsCarryingTheRegistration_AreHonoured()
    {
        // Options derived from TemporalAgentJsonUtilities.DefaultOptions carry the modifier, so the
        // caller's customization is used rather than discarded.
        var derived = new JsonSerializerOptions(TemporalAgentJsonUtilities.DefaultOptions);
        derived.MakeReadOnly();

        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.AppendHistoryEntry(Request("c1", "hello"));

        var agent = CreateAgent();
        var restored = Assert.IsType<TemporalAgentSession>(
            await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session, derived), derived));

        Assert.IsType<AgentSessionRequest>(Assert.Single(restored.History));
    }
}
