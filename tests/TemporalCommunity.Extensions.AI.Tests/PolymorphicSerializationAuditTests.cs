using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Converters;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Proactive audit for the "wrapper-type-with-polymorphic-subtypes silently drops on STJ
/// round-trip" bug class. Three instances of this bug have been caught reactively during
/// sample reviews:
/// <list type="bullet">
///   <item><c>GeneratedEmbeddings.Usage</c> (wrapper around <see cref="System.Collections.ObjectModel.Collection{T}"/>; side-properties dropped)</item>
///   <item><c>GeneratedEmbeddings.AdditionalProperties</c> (same root cause)</item>
///   <item><c>ChatOptions.Tools</c> (polymorphic <c>IList&lt;AITool&gt;</c>; collapses to <see langword="null"/>)</item>
/// </list>
/// Each instance was a silent data-loss bug: no exception, no warning, just lossy round-trip.
///
/// <para>
/// Audit methodology — for every type registered in <see cref="DurableAIJsonContext"/>
/// (or that flows through one of its registered types via a property), exercise a populated
/// representative instance through <see cref="DurableAIDataConverter.Instance"/> and assert
/// the polymorphic surfaces survive. Each test is named with the prefix
/// <c>Audit_{ContainerType}_{Member}_RoundTrip</c> for fast grep + triage. PASS = not a
/// regression site; FAIL = either a real bug (filed as Neo follow-up with hot-path caller)
/// or latent (registered but no production caller routes through the field).
/// </para>
/// </summary>
public class PolymorphicSerializationAuditTests
{
    private static readonly IPayloadConverter s_converter = DurableAIDataConverter.Instance.PayloadConverter;

    private static T RoundTrip<T>(T value) where T : class
    {
        var payload = s_converter.ToPayload(value);
        return (T)s_converter.ToValue(payload, typeof(T))!;
    }

    // ─── ChatOptions polymorphic members (other than Tools, which has its own converter) ──────

    /// <summary>
    /// <see cref="ChatOptions.ResponseFormat"/> is abstract — known subtypes include
    /// <c>ChatResponseFormatText</c>, <c>ChatResponseFormatJson</c>. Forwarded verbatim
    /// into <c>DurableChatInput.Options</c> by <c>DurableChatClient.PrepareOptionsForActivity</c>
    /// (DurableChatClient.cs:205). If this dies on round-trip, structured-output is broken
    /// over the wire.
    /// </summary>
    [Fact]
    public void Audit_ChatOptions_ResponseFormat_Json_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json,
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ResponseFormat);
        Assert.IsType<ChatResponseFormatJson>(deserialized.Options.ResponseFormat);
    }

    [Fact]
    public void Audit_ChatOptions_ResponseFormat_Text_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Text,
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ResponseFormat);
        Assert.IsType<ChatResponseFormatText>(deserialized.Options.ResponseFormat);
    }

    /// <summary>
    /// <see cref="ChatOptions.ToolMode"/> is abstract — known subtypes include
    /// <c>AutoChatToolMode</c>, <c>RequiredChatToolMode</c>, <c>NoneChatToolMode</c>.
    /// Forwarded into <c>DurableChatInput.Options</c> alongside ResponseFormat
    /// (DurableChatClient.cs:207). If this collapses to <see langword="null"/>, samples
    /// that pin a forced tool call (e.g. <c>ToolMode = ChatToolMode.RequireSpecific("x")</c>)
    /// degrade to Auto behavior over the wire.
    /// </summary>
    [Fact]
    public void Audit_ChatOptions_ToolMode_Auto_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ToolMode = ChatToolMode.Auto,
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ToolMode);
        Assert.IsType<AutoChatToolMode>(deserialized.Options.ToolMode);
    }

    [Fact]
    public void Audit_ChatOptions_ToolMode_RequireAny_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ToolMode = ChatToolMode.RequireAny,
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ToolMode);
        Assert.IsType<RequiredChatToolMode>(deserialized.Options.ToolMode);
    }

    [Fact]
    public void Audit_ChatOptions_ToolMode_RequireSpecific_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ToolMode = ChatToolMode.RequireSpecific("my_tool"),
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ToolMode);
        var required = Assert.IsType<RequiredChatToolMode>(deserialized.Options.ToolMode);
        Assert.Equal("my_tool", required.RequiredFunctionName);
    }

    [Fact]
    public void Audit_ChatOptions_ToolMode_None_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                ToolMode = ChatToolMode.None,
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.ToolMode);
        Assert.IsType<NoneChatToolMode>(deserialized.Options.ToolMode);
    }

    /// <summary>
    /// <see cref="ChatOptions.AdditionalProperties"/> is an
    /// <see cref="AdditionalPropertiesDictionary"/> (string→object). Values are
    /// polymorphic by definition — any boxed primitive, list, or POCO. The library
    /// uses this for its own per-request overrides (timeout, max-retry, client-key
    /// via <c>TemporalChatOptionsExtensions</c>) and end-users can stuff arbitrary
    /// metadata in here. If this collapses, per-request overrides silently revert
    /// to worker defaults.
    /// </summary>
    [Fact]
    public void Audit_ChatOptions_AdditionalProperties_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["string_key"] = "string_value",
                    ["int_key"] = 42,
                    ["bool_key"] = true,
                },
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.AdditionalProperties);
        Assert.True(deserialized.Options.AdditionalProperties!.ContainsKey("string_key"));
        Assert.True(deserialized.Options.AdditionalProperties.ContainsKey("int_key"));
        Assert.True(deserialized.Options.AdditionalProperties.ContainsKey("bool_key"));
    }

    /// <summary>
    /// <see cref="ChatOptions.StopSequences"/> is <see cref="IList{T}"/> of <see cref="string"/>
    /// (non-polymorphic, but a wrapper-around-list — verify it survives). Not a
    /// suspect under the bug class, but cheap to verify and catches regressions in the
    /// <c>ChatOptionsToolsJsonConverter</c> sibling-options flow.
    /// </summary>
    [Fact]
    public void Audit_ChatOptions_StopSequences_RoundTrip()
    {
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions
            {
                StopSequences = new List<string> { "STOP", "END" },
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.NotNull(deserialized.Options!.StopSequences);
        Assert.Equal(2, deserialized.Options.StopSequences!.Count);
        Assert.Contains("STOP", deserialized.Options.StopSequences);
        Assert.Contains("END", deserialized.Options.StopSequences);
    }

    // ─── EmbeddingGenerationOptions polymorphic members ────────────────────────────────────

    /// <summary>
    /// <see cref="EmbeddingGenerationOptions.AdditionalProperties"/> mirrors
    /// <see cref="ChatOptions.AdditionalProperties"/> in shape. Flows over the wire as part
    /// of <c>DurableEmbeddingInput.Options</c>. Same polymorphic-Object risk.
    /// </summary>
    [Fact]
    public void Audit_EmbeddingGenerationOptions_AdditionalProperties_RoundTrip()
    {
        var input = new DurableEmbeddingInput
        {
            Values = new List<string> { "hello" },
            Options = new EmbeddingGenerationOptions
            {
                ModelId = "text-embedding-3-small",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["dimensions"] = 512,
                    ["user_tag"] = "audit",
                },
            },
        };

        var deserialized = RoundTrip(input);

        Assert.NotNull(deserialized.Options);
        Assert.Equal("text-embedding-3-small", deserialized.Options!.ModelId);
        Assert.NotNull(deserialized.Options.AdditionalProperties);
        Assert.True(deserialized.Options.AdditionalProperties!.ContainsKey("dimensions"));
        Assert.True(deserialized.Options.AdditionalProperties.ContainsKey("user_tag"));
    }

    // ─── ChatResponse — return type of the activity, persisted in DurableSessionResponse ──

    /// <summary>
    /// <see cref="ChatResponse.AdditionalProperties"/> — same polymorphic-Object risk as
    /// <see cref="ChatOptions.AdditionalProperties"/>. <c>ChatResponse</c> is the return
    /// type of <c>DurableChatActivities.GetResponseAsync</c>; it crosses the activity
    /// boundary on every chat call.
    /// </summary>
    [Fact]
    public void Audit_ChatResponse_AdditionalProperties_RoundTrip()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
        {
            ModelId = "gpt-4o",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["service_tier"] = "premium",
                ["latency_ms"] = 142,
            },
        };

        var deserialized = RoundTrip(response);

        Assert.NotNull(deserialized.AdditionalProperties);
        Assert.True(deserialized.AdditionalProperties!.ContainsKey("service_tier"));
        Assert.True(deserialized.AdditionalProperties.ContainsKey("latency_ms"));
    }

    /// <summary>
    /// <see cref="ChatResponse.Usage"/> is <see cref="UsageDetails"/>, which itself contains
    /// <see cref="UsageDetails.AdditionalCounts"/> — a polymorphic dictionary (string→long).
    /// The values are non-polymorphic so this should round-trip cleanly; but
    /// <c>UsageDetails.AdditionalProperties</c> is the polymorphic-Object slot. Verify both.
    /// </summary>
    [Fact]
    public void Audit_ChatResponse_Usage_AdditionalProperties_RoundTrip()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                AdditionalCounts = new Microsoft.Extensions.AI.AdditionalPropertiesDictionary<long>
                {
                    ["cache_hit_tokens"] = 5,
                    ["reasoning_tokens"] = 7,
                },
            },
        };

        var deserialized = RoundTrip(response);

        Assert.NotNull(deserialized.Usage);
        Assert.Equal(10, deserialized.Usage!.InputTokenCount);
        Assert.NotNull(deserialized.Usage.AdditionalCounts);
        Assert.True(deserialized.Usage.AdditionalCounts!.ContainsKey("cache_hit_tokens"));
        Assert.True(deserialized.Usage.AdditionalCounts.ContainsKey("reasoning_tokens"));
        Assert.Equal(5L, deserialized.Usage.AdditionalCounts["cache_hit_tokens"]);
    }

    // ─── ChatMessage polymorphic surfaces (Contents is the canonical case) ─────────────────

    /// <summary>
    /// <see cref="ChatMessage.AdditionalProperties"/> — same polymorphic-Object risk.
    /// <c>ChatMessage</c> is the most-trafficked type in the library (every request, every
    /// response, every history entry).
    /// </summary>
    [Fact]
    public void Audit_ChatMessage_AdditionalProperties_RoundTrip()
    {
        var msg = new ChatMessage(ChatRole.User, "hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["client_request_id"] = "req-abc",
                ["priority"] = 5,
            },
        };
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage> { msg },
        };

        var deserialized = RoundTrip(input);

        var deserializedMsg = Assert.Single(deserialized.Messages);
        Assert.NotNull(deserializedMsg.AdditionalProperties);
        Assert.True(deserializedMsg.AdditionalProperties!.ContainsKey("client_request_id"));
        Assert.True(deserializedMsg.AdditionalProperties.ContainsKey("priority"));
    }

    /// <summary>
    /// <see cref="ChatMessage.Contents"/> with <see cref="UsageContent"/> — the assistant
    /// can emit usage metadata as an inline content block (rare but valid). Confirm the
    /// MEAI <c>AIContent</c> polymorphism handles this subtype.
    /// </summary>
    [Fact]
    public void Audit_ChatMessage_Contents_UsageContent_RoundTrip()
    {
        var usageContent = new UsageContent(new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 50,
        });
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, new List<AIContent> { usageContent }),
            },
        };

        var deserialized = RoundTrip(input);

        var msg = Assert.Single(deserialized.Messages);
        var content = Assert.Single(msg.Contents);
        var uc = Assert.IsType<UsageContent>(content);
        Assert.NotNull(uc.Details);
        Assert.Equal(100, uc.Details.InputTokenCount);
        Assert.Equal(50, uc.Details.OutputTokenCount);
    }

    /// <summary>
    /// <see cref="ChatMessage.Contents"/> with <see cref="TextReasoningContent"/> — reasoning
    /// blocks from "thinking" models (o1, Claude extended thinking).
    /// </summary>
    [Fact]
    public void Audit_ChatMessage_Contents_TextReasoningContent_RoundTrip()
    {
        var reasoning = new TextReasoningContent("Let me think step by step...");
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, new List<AIContent> { reasoning }),
            },
        };

        var deserialized = RoundTrip(input);

        var msg = Assert.Single(deserialized.Messages);
        var content = Assert.Single(msg.Contents);
        var trc = Assert.IsType<TextReasoningContent>(content);
        Assert.Equal("Let me think step by step...", trc.Text);
    }

    /// <summary>
    /// <see cref="ChatMessage.Contents"/> with <see cref="DataContent"/> — embedded binary
    /// (image, audio, etc.). Carries <c>Uri</c> + <c>MediaType</c>.
    /// </summary>
    [Fact]
    public void Audit_ChatMessage_Contents_DataContent_RoundTrip()
    {
        var data = new DataContent(new byte[] { 1, 2, 3, 4 }, "application/octet-stream");
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, new List<AIContent> { data }),
            },
        };

        var deserialized = RoundTrip(input);

        var msg = Assert.Single(deserialized.Messages);
        var content = Assert.Single(msg.Contents);
        var dc = Assert.IsType<DataContent>(content);
        Assert.Equal("application/octet-stream", dc.MediaType);
    }

    /// <summary>
    /// <see cref="ChatMessage.Contents"/> with <see cref="UriContent"/> — pointer to remote
    /// resource (image URL, document URL, etc.).
    /// </summary>
    [Fact]
    public void Audit_ChatMessage_Contents_UriContent_RoundTrip()
    {
        var uri = new UriContent(new Uri("https://example.com/image.png"), "image/png");
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, new List<AIContent> { uri }),
            },
        };

        var deserialized = RoundTrip(input);

        var msg = Assert.Single(deserialized.Messages);
        var content = Assert.Single(msg.Contents);
        var uc = Assert.IsType<UriContent>(content);
        Assert.Equal(new Uri("https://example.com/image.png"), uc.Uri);
        Assert.Equal("image/png", uc.MediaType);
    }

    /// <summary>
    /// <see cref="AIContent.AdditionalProperties"/> — every <c>AIContent</c> subtype
    /// inherits this slot. Verify on <see cref="TextContent"/> as the canonical case.
    /// </summary>
    [Fact]
    public void Audit_AIContent_AdditionalProperties_RoundTrip()
    {
        var text = new TextContent("hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["custom_tag"] = "value",
            },
        };
        var input = new DurableChatInput
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, new List<AIContent> { text }),
            },
        };

        var deserialized = RoundTrip(input);

        var msg = Assert.Single(deserialized.Messages);
        var tc = Assert.IsType<TextContent>(msg.Contents[0]);
        Assert.NotNull(tc.AdditionalProperties);
        Assert.True(tc.AdditionalProperties!.ContainsKey("custom_tag"));
    }

    // ─── DurableSessionEntry.AdditionalProperties (JsonExtensionData slot) ──────────────────

    /// <summary>
    /// <see cref="DurableSessionEntry.AdditionalProperties"/> is marked <c>[JsonExtensionData]</c>
    /// so it captures unknown fields verbatim. Verify it round-trips when populated with
    /// arbitrary JSON. Forward-compat slot — the workflow base preserves these in
    /// <c>StripMessagesFromEntry</c> (DurableChatWorkflowBase.cs:164, 171).
    /// </summary>
    [Fact]
    public void Audit_DurableSessionEntry_AdditionalProperties_RoundTrip()
    {
        var entry = new DurableSessionResponse
        {
            CorrelationId = "corr-extra",
            CreatedAt = new DateTimeOffset(2026, 5, 26, 0, 0, 0, TimeSpan.Zero),
            Messages = new List<ChatMessage>(),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future_field"] = JsonDocument.Parse("\"future_value\"").RootElement,
            },
        };

        var deserialized = RoundTrip<DurableSessionEntry>(entry);

        Assert.IsType<DurableSessionResponse>(deserialized);
        Assert.NotNull(deserialized.AdditionalProperties);
        Assert.True(deserialized.AdditionalProperties!.ContainsKey("future_field"));
    }

    // ─── DurableFunctionInput / Output ────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="DurableFunctionInput.Arguments"/> is <c>IDictionary&lt;string, object?&gt;</c>
    /// — polymorphic value side. Values may be boxed primitives, lists, or nested objects.
    /// Function-call dispatch hot path: written by <c>DurableChatWorkflow</c> when fanning
    /// out tool calls from a Pattern 3 LLM step.
    /// </summary>
    [Fact]
    public void Audit_DurableFunctionInput_Arguments_PolymorphicValues_RoundTrip()
    {
        var input = new DurableFunctionInput
        {
            FunctionName = "process",
            Arguments = new Dictionary<string, object?>
            {
                ["city"] = "Seattle",
                ["count"] = 5,
                ["enabled"] = true,
            },
        };

        var payload = s_converter.ToPayload(input);
        var deserialized = (DurableFunctionInput)s_converter.ToValue(payload, typeof(DurableFunctionInput))!;

        Assert.NotNull(deserialized.Arguments);
        Assert.True(deserialized.Arguments!.ContainsKey("city"));
        Assert.True(deserialized.Arguments.ContainsKey("count"));
        Assert.True(deserialized.Arguments.ContainsKey("enabled"));
    }

    /// <summary>
    /// <see cref="DurableFunctionOutput.Result"/> is bare <c>object?</c> — the most
    /// polymorphic surface in the library. Tools may return primitives, POCOs, anonymous
    /// objects, or null. Return value of the per-tool dispatch activity in Pattern 3
    /// (<c>DurableFunctionActivities.InvokeFunctionAsync</c>) — flows back into the
    /// workflow as a <c>FunctionResultContent</c> turn.
    /// </summary>
    [Fact]
    public void Audit_DurableFunctionOutput_Result_StringValue_RoundTrip()
    {
        var output = new DurableFunctionOutput { Result = "72°F and sunny" };

        var payload = s_converter.ToPayload(output);
        var deserialized = (DurableFunctionOutput)s_converter.ToValue(payload, typeof(DurableFunctionOutput))!;

        Assert.NotNull(deserialized.Result);
    }

    [Fact]
    public void Audit_DurableFunctionOutput_Result_ComplexObject_RoundTrip()
    {
        var output = new DurableFunctionOutput
        {
            Result = new Dictionary<string, object?>
            {
                ["temperature"] = 72,
                ["conditions"] = "sunny",
                ["humidity"] = 0.45,
            },
        };

        var payload = s_converter.ToPayload(output);
        var deserialized = (DurableFunctionOutput)s_converter.ToValue(payload, typeof(DurableFunctionOutput))!;

        Assert.NotNull(deserialized.Result);
    }

    // ─── DurableChatStepResult — Pattern 3 LLM-step result ──────────────────────────────────

    /// <summary>
    /// <see cref="DurableChatStepResult.AssistantMessage"/> is a <see cref="ChatMessage"/>
    /// — already covered by other tests. <see cref="DurableChatStepResult.ToolCalls"/>
    /// is <c>IReadOnlyList&lt;FunctionCallContent&gt;</c> — concrete type, no polymorphism
    /// risk, but verify the wrapping doesn't drop it.
    /// </summary>
    [Fact]
    public void Audit_DurableChatStepResult_ToolCalls_RoundTrip()
    {
        var stepResult = new DurableChatStepResult
        {
            IsFinal = false,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, "thinking..."),
            ToolCalls = new List<FunctionCallContent>
            {
                new("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "Seattle" }),
                new("call-2", "send_email", new Dictionary<string, object?> { ["to"] = "user@example.com" }),
            },
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
        };

        var payload = s_converter.ToPayload(stepResult);
        var deserialized = (DurableChatStepResult)s_converter.ToValue(payload, typeof(DurableChatStepResult))!;

        Assert.False(deserialized.IsFinal);
        Assert.NotNull(deserialized.ToolCalls);
        Assert.Equal(2, deserialized.ToolCalls!.Count);
        Assert.Equal("get_weather", deserialized.ToolCalls[0].Name);
        Assert.Equal("send_email", deserialized.ToolCalls[1].Name);
        Assert.NotNull(deserialized.Usage);
        Assert.Equal(100, deserialized.Usage!.InputTokenCount);
    }

    // ─── DurableChatWorkflowInput.HistoryReducer (JsonIgnore) and ToolActivityOptions ─────

    /// <summary>
    /// <see cref="DurableChatWorkflowInput.ToolActivityOptions"/> is
    /// <c>IReadOnlyDictionary&lt;string, ActivityOptions&gt;</c>. <c>ActivityOptions</c> is
    /// a concrete sealed Temporal SDK type, but the wrapper dict shape is exactly the kind
    /// of thing the bug class likes to chew on.
    /// </summary>
    [Fact]
    public void Audit_DurableChatWorkflowInput_ToolActivityOptions_RoundTrip()
    {
        var input = new DurableChatWorkflowInput
        {
            ToolActivityOptions = new Dictionary<string, Temporalio.Workflows.ActivityOptions>
            {
                ["get_weather"] = new Temporalio.Workflows.ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(30),
                },
                ["send_email"] = new Temporalio.Workflows.ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(1),
                },
            },
        };

        var payload = s_converter.ToPayload(input);
        var deserialized = (DurableChatWorkflowInput)s_converter.ToValue(payload, typeof(DurableChatWorkflowInput))!;

        Assert.NotNull(deserialized.ToolActivityOptions);
        Assert.Equal(2, deserialized.ToolActivityOptions!.Count);
        Assert.True(deserialized.ToolActivityOptions.ContainsKey("get_weather"));
        Assert.True(deserialized.ToolActivityOptions.ContainsKey("send_email"));
        Assert.Equal(TimeSpan.FromSeconds(30), deserialized.ToolActivityOptions["get_weather"].StartToCloseTimeout);
    }
}
