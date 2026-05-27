#pragma warning disable TA002 // CompactionMarkerEntry / compaction types are experimental

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// MAF-side companion to <c>Temporalio.Extensions.AI.Tests.PolymorphicSerializationAuditTests</c>.
/// Audits every <c>[JsonSerializable]</c> registration in <see cref="AgentSessionJsonContext"/>
/// (the Agents-library source-gen context) plus the runtime-modifier polymorphism added by
/// <see cref="TemporalAgentJsonUtilities"/> for the <c>"agent_request"</c> / <c>"agent_response"</c>
/// discriminators on <see cref="DurableSessionEntry"/>.
///
/// <para>
/// Uses <see cref="TemporalAgentDataConverter.Instance"/> — the converter the Agents library
/// actually wires onto the Temporal client for MAF-typed workflow histories. This is the only
/// correct entry point: <see cref="DurableAIDataConverter"/> alone does NOT carry the agent
/// derived-type modifier, so reusing the MEAI converter here would miss MAF-specific bugs.
/// </para>
///
/// <para>
/// <see cref="Session.TemporalAgentSession"/> is intentionally NOT audited: CLAUDE.md explicitly
/// flags that it is not part of any source-gen context, that
/// <c>DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))</c> is unsupported, and that
/// <c>SerializeStateBag()</c> delegates to <c>StateBag.Serialize()</c>. The supported StateBag
/// wire path is <c>JsonElement?</c> (carried on <see cref="AgentStepInput.SerializedStateBag"/>
/// / <see cref="AgentWorkflowInput.CarriedStateBag"/>); <see cref="JsonElement"/> is registered
/// in the source-gen context already and is not polymorphic, so no targeted test is required.
/// </para>
/// </summary>
public class PolymorphicSerializationAuditTests
{
    private static readonly IPayloadConverter s_converter = TemporalAgentDataConverter.Instance.PayloadConverter;

    private static T RoundTrip<T>(T value) where T : class
    {
        var payload = s_converter.ToPayload(value);
        return (T)s_converter.ToValue(payload, typeof(T))!;
    }

    // ─── DurableSessionEntry runtime-modifier polymorphism ──────────────────────────────
    // The base class declares "ai_request" / "ai_response" / "compaction-marker" via
    // [JsonDerivedType]. TemporalAgentJsonUtilities adds "agent_request" / "agent_response"
    // at runtime via WithAddedModifier. Verify both discriminators survive a base-typed
    // round-trip — that's the wire shape AgentWorkflow's history serializer actually uses.

    [Fact]
    public void Audit_DurableSessionEntry_AgentRequest_DerivedTypeRoundTrip()
    {
        DurableSessionEntry original = new AgentSessionRequest
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            OrchestrationId = "orch-1",
            ResponseType = "json",
            ResponseSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
        };

        var roundTripped = RoundTrip(original);

        var asRequest = Assert.IsType<AgentSessionRequest>(roundTripped);
        Assert.Equal("corr-1", asRequest.CorrelationId);
        Assert.Equal("orch-1", asRequest.OrchestrationId);
        Assert.Equal("json", asRequest.ResponseType);
        Assert.NotNull(asRequest.ResponseSchema);
        Assert.Equal(JsonValueKind.Object, asRequest.ResponseSchema!.Value.ValueKind);
    }

    [Fact]
    public void Audit_DurableSessionEntry_AgentResponse_DerivedTypeRoundTrip()
    {
        DurableSessionEntry original = new AgentSessionResponse
        {
            CorrelationId = "corr-2",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage> { new(ChatRole.Assistant, "ok") },
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 34 },
        };

        var roundTripped = RoundTrip(original);

        var asResponse = Assert.IsType<AgentSessionResponse>(roundTripped);
        Assert.Equal("corr-2", asResponse.CorrelationId);
        Assert.NotNull(asResponse.Usage);
        Assert.Equal(12, asResponse.Usage!.InputTokenCount);
        Assert.Equal(34, asResponse.Usage.OutputTokenCount);
    }

    // ─── AgentSessionRequest direct-type polymorphic members ────────────────────────────

    /// <summary>
    /// <see cref="AgentSessionRequest.ResponseSchema"/> is <see cref="JsonElement"/>?, a
    /// structural type — not polymorphic, but a frequent regression site because STJ's
    /// default Options can serialize it as a string or as a nested object depending on the
    /// resolver. Verify the schema survives shape-preserving.
    /// </summary>
    [Fact]
    public void Audit_AgentSessionRequest_ResponseSchema_RoundTrip()
    {
        var original = new AgentSessionRequest
        {
            CorrelationId = "corr-rs",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage> { new(ChatRole.User, "x") },
            ResponseSchema = JsonDocument.Parse(
                """{"type":"object","properties":{"name":{"type":"string"}}}""").RootElement,
        };

        var roundTripped = RoundTrip(original);

        Assert.NotNull(roundTripped.ResponseSchema);
        var schema = roundTripped.ResponseSchema!.Value;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string",
            schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
    }

    // ─── AgentSessionResponse.Usage (UsageDetails — wrapper-with-side-properties class) ─

    /// <summary>
    /// <see cref="UsageDetails"/> is the same class family that produced two reactive bugs on
    /// the MEAI side (<c>GeneratedEmbeddings.Usage</c> / <c>.AdditionalProperties</c>). It has
    /// a polymorphic collection-like surface (<see cref="UsageDetails.AdditionalCounts"/>) and
    /// a free-form <see cref="UsageDetails.AdditionalProperties"/> bag. Verify both survive a
    /// derived-type round-trip on the MAF response shape, which is the wire location where
    /// MAF stores token usage in workflow history.
    /// </summary>
    [Fact]
    public void Audit_AgentSessionResponse_Usage_AdditionalCounts_RoundTrip()
    {
        var original = new AgentSessionResponse
        {
            CorrelationId = "corr-uc",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage> { new(ChatRole.Assistant, "out") },
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 200,
                TotalTokenCount = 300,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_read"] = 50 },
            },
        };

        var roundTripped = RoundTrip(original);

        Assert.NotNull(roundTripped.Usage);
        Assert.Equal(100, roundTripped.Usage!.InputTokenCount);
        Assert.Equal(200, roundTripped.Usage.OutputTokenCount);
        Assert.Equal(300, roundTripped.Usage.TotalTokenCount);
        Assert.NotNull(roundTripped.Usage.AdditionalCounts);
        Assert.True(roundTripped.Usage.AdditionalCounts!.ContainsKey("cache_read"));
        Assert.Equal(50L, roundTripped.Usage.AdditionalCounts["cache_read"]);
    }

    // ─── AgentStepInput polymorphic members ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="AgentStepInput.Request"/>.<see cref="RunRequest.ResponseFormat"/> is abstract.
    /// MAF callers can pass <c>ChatResponseFormat.Json</c> via the structured-output extension;
    /// if this drops on the workflow-↔-activity boundary, structured-output is broken in MAF.
    /// </summary>
    [Fact]
    public void Audit_AgentStepInput_Request_ResponseFormat_Json_RoundTrip()
    {
        var input = new AgentStepInput
        {
            AgentName = "test-agent",
            Request = new RunRequest("hi") { CorrelationId = "c1", ResponseFormat = ChatResponseFormat.Json },
            AccumulatedMessages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            IsFirstStep = true,
        };

        var roundTripped = RoundTrip(input);

        Assert.NotNull(roundTripped.Request.ResponseFormat);
        Assert.IsType<ChatResponseFormatJson>(roundTripped.Request.ResponseFormat);
    }

    [Fact]
    public void Audit_AgentStepInput_AccumulatedMessages_FunctionCallContent_RoundTrip()
    {
        var input = new AgentStepInput
        {
            AgentName = "test-agent",
            Request = new RunRequest("call my tool") { CorrelationId = "c2" },
            AccumulatedMessages = new List<ChatMessage>
            {
                new(ChatRole.User, "call my tool"),
                new(ChatRole.Assistant,
                    [new FunctionCallContent(callId: "call-xyz", name: "do_thing",
                        arguments: new Dictionary<string, object?> { ["a"] = 1 })]),
                new(ChatRole.Tool,
                    [new FunctionResultContent(callId: "call-xyz", result: "42")]),
            },
            IsFirstStep = false,
        };

        var roundTripped = RoundTrip(input);

        Assert.Equal(3, roundTripped.AccumulatedMessages.Count);
        var call = Assert.IsType<FunctionCallContent>(roundTripped.AccumulatedMessages[1].Contents[0]);
        Assert.Equal("call-xyz", call.CallId);
        Assert.Equal("do_thing", call.Name);
        var result = Assert.IsType<FunctionResultContent>(roundTripped.AccumulatedMessages[2].Contents[0]);
        Assert.Equal("call-xyz", result.CallId);
    }

    [Fact]
    public void Audit_AgentStepInput_SerializedStateBag_RoundTrip()
    {
        var bag = JsonDocument.Parse("""{"thread_id":"t-7","flags":[1,2,3]}""").RootElement;
        var input = new AgentStepInput
        {
            AgentName = "a",
            Request = new RunRequest("hi") { CorrelationId = "c3" },
            AccumulatedMessages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            SerializedStateBag = bag,
        };

        var roundTripped = RoundTrip(input);

        Assert.NotNull(roundTripped.SerializedStateBag);
        Assert.Equal("t-7", roundTripped.SerializedStateBag!.Value.GetProperty("thread_id").GetString());
        Assert.Equal(3, roundTripped.SerializedStateBag.Value.GetProperty("flags").GetArrayLength());
    }

    // ─── AgentStepResult polymorphic members ────────────────────────────────────────────

    [Fact]
    public void Audit_AgentStepResult_AssistantMessage_RoundTrip()
    {
        var result = new AgentStepResult
        {
            IsFinal = true,
            AssistantMessage = new ChatMessage(ChatRole.Assistant,
                [new TextContent("final answer"), new TextReasoningContent("because")]),
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 7 },
        };

        var roundTripped = RoundTrip(result);

        Assert.True(roundTripped.IsFinal);
        Assert.Equal(2, roundTripped.AssistantMessage.Contents.Count);
        Assert.IsType<TextContent>(roundTripped.AssistantMessage.Contents[0]);
        Assert.IsType<TextReasoningContent>(roundTripped.AssistantMessage.Contents[1]);
        Assert.NotNull(roundTripped.Usage);
        Assert.Equal(5, roundTripped.Usage!.InputTokenCount);
    }

    [Fact]
    public void Audit_AgentStepResult_ToolCalls_RoundTrip()
    {
        var result = new AgentStepResult
        {
            IsFinal = false,
            AssistantMessage = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId: "cid-1", name: "f1"),
                 new FunctionCallContent(callId: "cid-2", name: "f2",
                    arguments: new Dictionary<string, object?> { ["x"] = "y" })]),
            ToolCalls = new List<FunctionCallContent>
            {
                new(callId: "cid-1", name: "f1"),
                new(callId: "cid-2", name: "f2",
                    arguments: new Dictionary<string, object?> { ["x"] = "y" }),
            },
        };

        var roundTripped = RoundTrip(result);

        Assert.False(roundTripped.IsFinal);
        Assert.NotNull(roundTripped.ToolCalls);
        Assert.Equal(2, roundTripped.ToolCalls!.Count);
        Assert.Equal("cid-1", roundTripped.ToolCalls[0].CallId);
        Assert.Equal("f2", roundTripped.ToolCalls[1].Name);
        Assert.Equal("y", roundTripped.ToolCalls[1].Arguments!["x"]?.ToString());
    }

    [Fact]
    public void Audit_AgentStepResult_UpdatedStateBag_RoundTrip()
    {
        var bag = JsonDocument.Parse("""{"k":"v"}""").RootElement;
        var result = new AgentStepResult
        {
            IsFinal = true,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, "ok"),
            UpdatedStateBag = bag,
        };

        var roundTripped = RoundTrip(result);

        Assert.NotNull(roundTripped.UpdatedStateBag);
        Assert.Equal("v", roundTripped.UpdatedStateBag!.Value.GetProperty("k").GetString());
    }

    // ─── InvokeAgentToolInput / Result polymorphic members ──────────────────────────────

    [Fact]
    public void Audit_InvokeAgentToolInput_Arguments_PolymorphicValues_RoundTrip()
    {
        var input = new InvokeAgentToolInput
        {
            AgentName = "a",
            ToolName = "compute",
            Arguments = new Dictionary<string, object?>
            {
                ["str"] = "hello",
                ["num"] = 42,
                ["bool"] = true,
                ["null"] = null,
            },
            CallId = "call-1",
        };

        var roundTripped = RoundTrip(input);

        Assert.NotNull(roundTripped.Arguments);
        Assert.Equal(4, roundTripped.Arguments!.Count);
        Assert.Equal("hello", roundTripped.Arguments["str"]?.ToString());
        Assert.Equal("call-1", roundTripped.CallId);
    }

    [Fact]
    public void Audit_InvokeAgentToolResult_Result_RoundTrip()
    {
        var result = new InvokeAgentToolResult
        {
            Result = new Dictionary<string, object?> { ["answer"] = "42" },
            CallId = "call-1",
        };

        var roundTripped = RoundTrip(result);

        Assert.NotNull(roundTripped.Result);
        Assert.Equal("call-1", roundTripped.CallId);
    }

    // ─── AppendAgentTurnInput (MAF AgentResponse over the wire) ─────────────────────────

    /// <summary>
    /// <see cref="AppendAgentTurnInput.TurnResponse"/> is <see cref="AgentResponse"/> from
    /// <c>Microsoft.Agents.AI</c>. The type is NOT registered in <see cref="AgentSessionJsonContext"/>
    /// (only <see cref="AppendAgentTurnInput"/> is). The audit verifies whether the polymorphic
    /// chat-content surface inside <c>AgentResponse.Messages</c> survives a wrapper round-trip —
    /// this activity is dispatched from <c>AgentWorkflow</c> when an external history store is
    /// configured (<c>AppendAgentTurnAsync</c>).
    /// </summary>
    [Fact]
    public void Audit_AppendAgentTurnInput_TurnResponse_Messages_RoundTrip()
    {
        var input = new AppendAgentTurnInput
        {
            AgentName = "a",
            SessionId = "s1",
            Request = new RunRequest("go") { CorrelationId = "c-append" },
            TurnResponse = new AgentResponse
            {
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.Assistant,
                        [new FunctionCallContent(callId: "fc-1", name: "t",
                            arguments: new Dictionary<string, object?> { ["q"] = 1 })]),
                    new(ChatRole.Tool, [new FunctionResultContent(callId: "fc-1", result: "ok")]),
                    new(ChatRole.Assistant, "done"),
                },
                Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22 },
                ResponseId = "resp-9",
            },
        };

        var roundTripped = RoundTrip(input);

        Assert.NotNull(roundTripped.TurnResponse);
        Assert.Equal(3, roundTripped.TurnResponse!.Messages.Count);
        var fc = Assert.IsType<FunctionCallContent>(roundTripped.TurnResponse.Messages[0].Contents[0]);
        Assert.Equal("fc-1", fc.CallId);
        Assert.Equal("t", fc.Name);
        var fr = Assert.IsType<FunctionResultContent>(roundTripped.TurnResponse.Messages[1].Contents[0]);
        Assert.Equal("fc-1", fr.CallId);
        Assert.NotNull(roundTripped.TurnResponse.Usage);
        Assert.Equal(11, roundTripped.TurnResponse.Usage!.InputTokenCount);
        Assert.Equal("resp-9", roundTripped.TurnResponse.ResponseId);
    }

    // ─── Compaction wire types (Experimental TA002) ─────────────────────────────────────

    [Fact]
    public void Audit_RunCompactionSummaryInput_Messages_RoundTrip()
    {
        var input = new RunCompactionSummaryInput
        {
            AgentName = "a",
            SummarizationPrompt = new List<ChatMessage>
            {
                new(ChatRole.System, "Summarize:"),
                new(ChatRole.User,
                    [new TextContent("turn 1"), new TextReasoningContent("thinking")]),
                new(ChatRole.Assistant,
                    [new FunctionCallContent(callId: "x", name: "f")]),
            },
            ChatClientKey = "summarizer",
            ModelIdOverride = "gpt-4o-mini",
        };

        var roundTripped = RoundTrip(input);

        Assert.Equal(3, roundTripped.SummarizationPrompt.Count);
        Assert.Equal(2, roundTripped.SummarizationPrompt[1].Contents.Count);
        Assert.IsType<TextContent>(roundTripped.SummarizationPrompt[1].Contents[0]);
        Assert.IsType<TextReasoningContent>(roundTripped.SummarizationPrompt[1].Contents[1]);
        Assert.IsType<FunctionCallContent>(roundTripped.SummarizationPrompt[2].Contents[0]);
        Assert.Equal("summarizer", roundTripped.ChatClientKey);
    }

    [Fact]
    public void Audit_RunCompactionSummaryResult_SummaryMessages_RoundTrip()
    {
        var result = new RunCompactionSummaryResult
        {
            SummaryMessages = new List<ChatMessage>
            {
                new(ChatRole.Assistant,
                    [new TextContent("rollup"), new TextReasoningContent("derivation")]),
            },
            ModelIdUsed = "gpt-4o-mini",
            InputTokenCount = 500,
            OutputTokenCount = 50,
        };

        var roundTripped = RoundTrip(result);

        Assert.Single(roundTripped.SummaryMessages);
        Assert.Equal(2, roundTripped.SummaryMessages[0].Contents.Count);
        Assert.IsType<TextContent>(roundTripped.SummaryMessages[0].Contents[0]);
        Assert.IsType<TextReasoningContent>(roundTripped.SummaryMessages[0].Contents[1]);
        Assert.Equal(500, roundTripped.InputTokenCount);
    }

    [Fact]
    public void Audit_DurableSessionEntry_CompactionMarker_RoundTrip()
    {
        DurableSessionEntry original = new CompactionMarkerEntry
        {
            CorrelationId = "marker-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, "summary text"),
            },
            CompactedMessageIds = new[] { "id-1", "id-2", "id-3" },
            Strategy = "summarization",
            ModelId = "gpt-4o-mini",
            OriginatingTurnCorrelationIds = new[] { "turn-1", "turn-2" },
        };

        var roundTripped = RoundTrip(original);

        var marker = Assert.IsType<CompactionMarkerEntry>(roundTripped);
        Assert.Equal("marker-1", marker.CorrelationId);
        Assert.Equal(3, marker.CompactedMessageIds.Count);
        Assert.Equal("summarization", marker.Strategy);
        Assert.Equal("gpt-4o-mini", marker.ModelId);
        Assert.Equal(2, marker.OriginatingTurnCorrelationIds.Count);
    }

    // ─── List<DurableSessionEntry> — registered explicitly in the source-gen context ────

    [Fact]
    public void Audit_ListOfDurableSessionEntry_MixedSubtypes_RoundTrip()
    {
        var original = new List<DurableSessionEntry>
        {
            new DurableSessionRequest
            {
                CorrelationId = "r1",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new List<ChatMessage> { new(ChatRole.User, "u") },
            },
            new AgentSessionRequest
            {
                CorrelationId = "r2",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new List<ChatMessage> { new(ChatRole.User, "u2") },
                OrchestrationId = "orch-x",
                ResponseType = "text",
            },
            new AgentSessionResponse
            {
                CorrelationId = "r2",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = new List<ChatMessage> { new(ChatRole.Assistant, "a") },
                Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2 },
            },
            new CompactionMarkerEntry
            {
                CorrelationId = "m1",
                CreatedAt = DateTimeOffset.UtcNow,
                CompactedMessageIds = new[] { "r1" },
                Strategy = "truncation",
                ModelId = string.Empty,
                OriginatingTurnCorrelationIds = new[] { "turn-1" },
            },
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(4, roundTripped.Count);
        Assert.IsType<DurableSessionRequest>(roundTripped[0]);
        var agentReq = Assert.IsType<AgentSessionRequest>(roundTripped[1]);
        Assert.Equal("orch-x", agentReq.OrchestrationId);
        var agentResp = Assert.IsType<AgentSessionResponse>(roundTripped[2]);
        Assert.NotNull(agentResp.Usage);
        Assert.Equal(2, agentResp.Usage!.OutputTokenCount);
        var marker = Assert.IsType<CompactionMarkerEntry>(roundTripped[3]);
        Assert.Equal("truncation", marker.Strategy);
    }
}
