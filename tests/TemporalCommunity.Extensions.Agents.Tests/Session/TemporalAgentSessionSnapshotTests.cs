using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Converters;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Session;

/// <summary>
/// Option D Phase 1a — wire-contract prototype gates for <see cref="TemporalAgentSessionSnapshot"/>.
/// These gates validate the DTO in isolation, before the public agent serialization boundary is
/// switched over to it in Phase 1b.
/// </summary>
public class TemporalAgentSessionSnapshotTests
{
    // Gate 1: source-gen resolver-origin.
    // A non-None JsonTypeInfo.Kind is not sufficient evidence — the reflection resolver also
    // produces metadata with a non-None kind. Asserting the originating resolver is the only
    // check that actually proves the generated context won.
    [Fact]
    public void Gate1_Snapshot_UsesGeneratedResolver_InDurableOptions()
    {
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions
            .GetTypeInfo(typeof(TemporalAgentSessionSnapshot));

        Assert.NotNull(typeInfo);
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
    }

    // Gate 2: polymorphic payload round-trip through the Temporal data converter,
    // covering a full request -> tool call -> tool result cycle.
    [Fact]
    public async Task Gate2_Snapshot_RoundTripsPolymorphicHistory_ThroughDataConverter()
    {
        var sessionId = new TemporalAgentSessionId("Assistant", "abc123");

        var request = new AgentSessionRequest
        {
            CorrelationId = "corr-1",
            CreatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            Messages = [new ChatMessage(ChatRole.User, "Hello")],
        };

        var toolCallMessage = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>
            {
                ["city"] = "Kingston",
            }),
        ]);
        var toolResultMessage = new ChatMessage(ChatRole.Tool, [
            new FunctionResultContent("call-1", "sunny, 30C"),
        ]);

        var response = new AgentSessionResponse
        {
            CorrelationId = "corr-1",
            CreatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 5, TimeSpan.Zero),
            Messages = [toolCallMessage, toolResultMessage],
        };

        var typedBag = new AgentSessionStateBag();
        typedBag.SetValue("temporal.working_set", new[] { "src/a.cs" });
        var stateBag = typedBag.Serialize();

        var snapshot = new TemporalAgentSessionSnapshot
        {
            SessionId = sessionId.WorkflowId,
            StateBag = stateBag,
            History = [request, response],
        };

        var converter = TemporalAgentDataConverter.Instance;
        var payload = await converter.ToPayloadAsync(snapshot);
        var restored = await converter.ToValueAsync<TemporalAgentSessionSnapshot>(payload);

        Assert.NotNull(restored);
        Assert.Equal(sessionId.WorkflowId, restored.SessionId);
        Assert.NotNull(restored.History);
        Assert.Equal(2, restored.History!.Count);

        // Discriminators survive: the entries come back as the MAF-specific subtypes, not the base.
        var restoredRequest = Assert.IsType<AgentSessionRequest>(restored.History[0]);
        var restoredResponse = Assert.IsType<AgentSessionResponse>(restored.History[1]);
        Assert.Equal("corr-1", restoredRequest.CorrelationId);
        Assert.Equal("Hello", restoredRequest.Messages[0].Text);

        // AIContent polymorphism survives: FunctionCallContent / FunctionResultContent do not
        // collapse into the base AIContent type.
        var call = Assert.IsType<FunctionCallContent>(restoredResponse.Messages[0].Contents[0]);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("call-1", call.CallId);
        Assert.True(call.Arguments!.TryGetValue("city", out var city));
        Assert.Equal("Kingston", city?.ToString());

        var result = Assert.IsType<FunctionResultContent>(restoredResponse.Messages[1].Contents[0]);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("sunny, 30C", result.Result?.ToString());

        // Load the round-tripped element back into a real bag, not just a raw JSON property read:
        // Phase 1c moves StateBag ownership onto the session, so "the element survived" is not
        // enough — it has to still be a bag.
        Assert.NotNull(restored.StateBag);
        var restoredBag = AgentSessionStateBag.Deserialize(restored.StateBag!.Value);
        Assert.True(restoredBag.TryGetValue<string[]>("temporal.working_set", out var workingSet));
        Assert.NotNull(workingSet);
        Assert.Equal(["src/a.cs"], workingSet);
    }

    // Gate 3: legacy payload compatibility — a snapshot persisted before session-owned history
    // existed carries no "history" member at all.
    [Fact]
    public void Gate3_LegacySnapshotWithoutHistory_Deserializes()
    {
        const string legacyJson = """
            {"sessionId":"ta-assistant-abc123","stateBag":{"k":"v"}}
            """;

        var snapshot = JsonSerializer.Deserialize<TemporalAgentSessionSnapshot>(
            legacyJson, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.NotNull(snapshot);
        Assert.Equal("ta-assistant-abc123", snapshot!.SessionId);
        Assert.Null(snapshot.History);
        Assert.NotNull(snapshot.StateBag);
        var restoredBag = AgentSessionStateBag.Deserialize(snapshot.StateBag!.Value);
        Assert.True(restoredBag.TryGetValue<string>("k", out var value));
        Assert.Equal("v", value);

        // The persisted ID is lossless: it parses back to the same agent name and key.
        var parsed = TemporalAgentSessionId.Parse(snapshot.SessionId);
        Assert.Equal("assistant", parsed.AgentName);
        Assert.Equal("abc123", parsed.Key);
    }

    // Gate 4: empty-state optimization — null optional members are omitted, not written as null.
    [Fact]
    public void Gate4_EmptySnapshot_OmitsNullMembers()
    {
        var snapshot = new TemporalAgentSessionSnapshot
        {
            SessionId = "ta-assistant-abc123",
        };

        var element = JsonSerializer.SerializeToElement(
            snapshot, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("ta-assistant-abc123", element.GetProperty("sessionId").GetString());
        Assert.False(element.TryGetProperty("stateBag", out _));
        Assert.False(element.TryGetProperty("history", out _));
        Assert.Single(element.EnumerateObject());
    }

    // Gate 4 (companion): omission must not depend on the caller's ambient options. MAF may hand
    // the agent a JsonSerializerOptions that does not set WhenWritingNull, so the ignore condition
    // is declared on the members themselves.
    [Fact]
    public void Gate4_EmptySnapshot_OmitsNullMembers_EvenWhenOptionsWriteNulls()
    {
        var writeNullOptions = new JsonSerializerOptions(TemporalAgentJsonUtilities.DefaultOptions)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };
        writeNullOptions.MakeReadOnly();

        var element = JsonSerializer.SerializeToElement(
            new TemporalAgentSessionSnapshot { SessionId = "ta-assistant-abc123" },
            writeNullOptions);

        Assert.False(element.TryGetProperty("stateBag", out _));
        Assert.False(element.TryGetProperty("history", out _));
    }
}
