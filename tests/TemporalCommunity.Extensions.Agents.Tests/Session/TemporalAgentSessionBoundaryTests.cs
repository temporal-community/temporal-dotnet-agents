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
        session.StateBag.SetValue("temporal.working_set", new[] { "src/a.cs" });
        session.AppendHistoryEntry(Request("c1", "first"));
        session.AppendHistoryEntry(Response("c1", "first reply"));
        session.AppendHistoryEntry(Request("c2", "second"));

        var serialized = await agent.SerializeSessionAsync(session);
        var restored = Assert.IsType<TemporalAgentSession>(await agent.DeserializeSessionAsync(serialized));

        Assert.Equal(session.SessionId, restored.SessionId);
        var restoredWorkingSet = restored.StateBag.GetValue<string[]>("temporal.working_set");
        Assert.NotNull(restoredWorkingSet);
        Assert.Equal(["src/a.cs"], restoredWorkingSet);

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
        // Byte-for-byte the shape the pre-change direct-session serializer emitted. The scalar
        // temporal.working_set is deliberate: this is the 0.14.2-era value, and it must still
        // restore. See WorkingSetContextProviderTests for what a current reader makes of it.
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
    // running the real `TemporalAgentSession.Serialize` against a built assembly — not
    // hand-guessed. A break here means sessions already persisted in workflow histories can no
    // longer be restored.

    [Fact]
    public async Task Legacy_EmptyStateBagObject_RestoresAsEmptyBag()
    {
        // The previous writer always emitted the property, so an empty bag serialized as "stateBag":{}.
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

    [Fact]
    public async Task ForeignOptions_KeepTheirEncoderAndDepthLimit()
    {
        // The fallback must not silently downgrade a caller's security-relevant settings. The
        // library default uses UnsafeRelaxedJsonEscaping, which leaves '<' unescaped — a caller
        // who chose a stricter encoder because they embed the payload somewhere must keep it, and
        // the payload now carries model- and tool-authored conversation text.
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            MaxDepth = 16,
        };
        strict.MakeReadOnly();

        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.AppendHistoryEntry(Request("c1", "</script><img src=x onerror=alert(1)>"));

        var agent = CreateAgent();
        var serialized = await agent.SerializeSessionAsync(session, strict);
        var raw = serialized.GetRawText();

        // Angle brackets escaped by the caller's encoder, not emitted raw by the library default.
        Assert.DoesNotContain("</script>", raw, StringComparison.Ordinal);
        Assert.Contains("\\u003C", raw, StringComparison.OrdinalIgnoreCase);

        // ...and the fallback still round-trips history, which is why it exists at all.
        var restored = Assert.IsType<TemporalAgentSession>(
            await agent.DeserializeSessionAsync(serialized, strict));
        Assert.IsType<AgentSessionRequest>(Assert.Single(restored.History));
    }

    [Fact]
    public async Task ForeignOptions_FallbackIsCachedPerCallerInstance()
    {
        // Building fresh options per call would discard System.Text.Json's per-instance metadata
        // cache and make every serialize pay full resolution cost.
        var foreign = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        foreign.MakeReadOnly();

        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.AppendHistoryEntry(Request("c1", "hello"));

        var agent = CreateAgent();
        var first = await agent.SerializeSessionAsync(session, foreign);
        var second = await agent.SerializeSessionAsync(session, foreign);

        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    // ─── Snapshot contract gating ───────────────────────────────────────────

    /// <summary>
    /// Builds reflection-backed options that DO declare the two MAF history discriminators —
    /// the case that satisfies a polymorphism-only check while still resolving the snapshot root
    /// through reflection.
    /// </summary>
    private static JsonSerializerOptions ReflectionOptionsWithAgentPolymorphism()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(DurableSessionEntry))
            {
                return;
            }

            typeInfo.PolymorphismOptions ??= new System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
            };
            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new System.Text.Json.Serialization.Metadata.JsonDerivedType(typeof(AgentSessionRequest), "agent_request"));
            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new System.Text.Json.Serialization.Metadata.JsonDerivedType(typeof(AgentSessionResponse), "agent_response"));
        });

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
        };
        options.MakeReadOnly();
        return options;
    }

    [Fact]
    public void SnapshotContract_RequiresGeneratedRoot_NotJustHistoryPolymorphism()
    {
        // The gap this closes: options can carry the agent_request / agent_response discriminators
        // and still resolve TemporalAgentSessionSnapshot itself through reflection. Accepting them
        // would serialize the snapshot root via DefaultJsonTypeInfoResolver — reintroducing exactly
        // the AOT and trimming exposure that registering the DTO in AgentSessionJsonContext removes.
        var mixed = ReflectionOptionsWithAgentPolymorphism();

        // Precondition: these options genuinely satisfy the history half of the contract, so the
        // test is exercising the snapshot-root half and not just a missing registration.
        var derivedTypes = mixed.GetTypeInfo(typeof(DurableSessionEntry)).PolymorphismOptions!.DerivedTypes;
        Assert.Contains(derivedTypes, d => d.DerivedType == typeof(AgentSessionRequest));
        Assert.Contains(derivedTypes, d => d.DerivedType == typeof(AgentSessionResponse));

        // ...and the snapshot root resolves through reflection under them.
        Assert.IsType<System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver>(
            mixed.GetTypeInfo(typeof(TemporalAgentSessionSnapshot)).OriginatingResolver);

        Assert.False(TemporalAgentSession.CanRoundTripSnapshotContract(mixed));
    }

    [Fact]
    public void SnapshotContract_AcceptsDefaultAndDerivedOptions()
    {
        Assert.True(TemporalAgentSession.CanRoundTripSnapshotContract(TemporalAgentJsonUtilities.DefaultOptions));

        var derived = new JsonSerializerOptions(TemporalAgentJsonUtilities.DefaultOptions);
        derived.MakeReadOnly();
        Assert.True(TemporalAgentSession.CanRoundTripSnapshotContract(derived));
    }

    [Fact]
    public void SnapshotContract_RejectsPlainReflectionOptions()
    {
        var reflection = new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        reflection.MakeReadOnly();

        Assert.False(TemporalAgentSession.CanRoundTripSnapshotContract(reflection));
    }

    [Fact]
    public async Task MixedOptions_StillRoundTripThroughTheGeneratedContract()
    {
        // Behavioural companion to the predicate test: the fallback must engage, so the payload is
        // still written by the generated contract and still restores its history subtypes.
        var mixed = ReflectionOptionsWithAgentPolymorphism();

        var session = new TemporalAgentSession(new TemporalAgentSessionId("Assistant", "abc123"));
        session.AppendHistoryEntry(Request("c1", "hello"));
        session.AppendHistoryEntry(Response("c1", "hi back"));

        var agent = CreateAgent();
        var serialized = await agent.SerializeSessionAsync(session, mixed);
        var restored = Assert.IsType<TemporalAgentSession>(
            await agent.DeserializeSessionAsync(serialized, mixed));

        Assert.Equal(2, restored.History.Count);
        Assert.IsType<AgentSessionRequest>(restored.History[0]);
        Assert.IsType<AgentSessionResponse>(restored.History[1]);

        // And the generated path can read it back without the caller's options at all, which is
        // the property that would break if the snapshot root had gone through reflection with a
        // divergent naming policy.
        var restoredByDefault = Assert.IsType<TemporalAgentSession>(
            await agent.DeserializeSessionAsync(serialized));
        Assert.Equal(2, restoredByDefault.History.Count);
    }
}
