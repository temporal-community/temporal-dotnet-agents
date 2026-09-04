using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableAIDataConverterTests
{
    public static TheoryData<AIContent> NewContentTypes => new()
    {
        new TextReasoningContent("thinking step"),
        new McpServerToolCallContent("call-1", "my_tool", "my-mcp-server"),
        new McpServerToolResultContent("call-1"),
        new ToolApprovalRequestContent("req-1", new FunctionCallContent("call-2", "approve_fn")),
        new ToolApprovalResponseContent("req-1", true, new FunctionCallContent("call-2", "approve_fn")),
        new ImageGenerationToolCallContent("call-3"),
        new ImageGenerationToolResultContent("call-3"),
        new CodeInterpreterToolCallContent("call-4"),
        new CodeInterpreterToolResultContent("call-4"),
        new WebSearchToolCallContent("call-5"),
        new WebSearchToolResultContent("call-5"),
        new HostedVectorStoreContent("vs-abc"),
        new HostedFileContent("file-xyz"),
    };

    [Theory]
    [MemberData(nameof(NewContentTypes))]
    public void RoundTrip_PreservesConcreteType(AIContent content)
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var payload = converter.ToPayload(content);
        var deserialized = converter.ToValue(payload, typeof(AIContent));

        Assert.IsType(content.GetType(), deserialized);
    }

    [Fact]
    public void Instance_IsNotNull()
    {
        Assert.NotNull(DurableAIDataConverter.Instance);
    }

    [Fact]
    public void Instance_HasPayloadConverter()
    {
        Assert.NotNull(DurableAIDataConverter.Instance.PayloadConverter);
    }

    [Fact]
    public void Instance_HasFailureConverter()
    {
        Assert.NotNull(DurableAIDataConverter.Instance.FailureConverter);
    }

    [Fact]
    public void PayloadConverter_CanSerializeChatMessage()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var message = new ChatMessage(ChatRole.User, "Hello!");

        var payload = converter.ToPayload(message);
        Assert.NotNull(payload);

        var deserialized = (ChatMessage)converter.ToValue(payload, typeof(ChatMessage))!;
        Assert.NotNull(deserialized);
        Assert.Equal(ChatRole.User, deserialized.Role);
        Assert.Equal("Hello!", deserialized.Text);
    }

    [Fact]
    public void PayloadConverter_PreservesAIContentPolymorphism()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var functionCall = new FunctionCallContent("call-1", "get_weather",
            new Dictionary<string, object?> { ["city"] = "Seattle" });
        var message = new ChatMessage(ChatRole.Assistant, [functionCall]);

        var payload = converter.ToPayload(message);
        var deserialized = (ChatMessage)converter.ToValue(payload, typeof(ChatMessage))!;

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Contents);
        var content = deserialized.Contents[0];
        Assert.IsType<FunctionCallContent>(content);

        var fc = (FunctionCallContent)content;
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("get_weather", fc.Name);
    }

    [Fact]
    public void DefaultPayloadConverter_PreservesFunctionCallAndResultContent()
    {
        // This is the exact manual-client boundary alleged to lose these MEAI content types.
        // Keep it separate from DurableAIDataConverter: it decides whether an AI-side startup
        // guard has a reproducible payload-contract basis.
        var message = new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "get_weather",
                    new Dictionary<string, object?> { ["city"] = "Seattle" }),
                new FunctionResultContent("call-1", new { temperature = 72 }),
            ]);

        var payload = DataConverter.Default.PayloadConverter.ToPayload(message);
        var restored = (ChatMessage)DataConverter.Default.PayloadConverter.ToValue(
            payload,
            typeof(ChatMessage))!;

        var call = Assert.IsType<FunctionCallContent>(restored.Contents[0]);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        var city = Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["city"]);
        Assert.Equal("Seattle", city.GetString());

        var result = Assert.IsType<FunctionResultContent>(restored.Contents[1]);
        Assert.Equal("call-1", result.CallId);
        var resultElement = Assert.IsType<System.Text.Json.JsonElement>(result.Result);
        Assert.Equal(72, resultElement.GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void PayloadConverter_CanSerializeChatResponse()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "World!")])
        {
            ModelId = "test-model",
        };

        var payload = converter.ToPayload(response);
        var deserialized = (ChatResponse)converter.ToValue(payload, typeof(ChatResponse))!;

        Assert.NotNull(deserialized);
        Assert.Equal("test-model", deserialized.ModelId);
        Assert.Single(deserialized.Messages);
    }
}
