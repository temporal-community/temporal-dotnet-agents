using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableStructuredOutputTests
{
    [Fact]
    public async Task SendStructuredAsync_RequestsSchemaAndPreservesResponseMetadata()
    {
        ChatOptions? capturedOptions = null;
        string? capturedRequestId = null;
        var responseEntry = CreateResponseEntry("""{"answer":"42"}""");
        var sessionClient = A.Fake<IDurableChatSessionClient>();
        A.CallTo(() => sessionClient.SendAsync(
                A<string>._,
                A<IEnumerable<ChatMessage>>._,
                A<ChatOptions?>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<CancellationToken>._))
            .Invokes((string _, IEnumerable<ChatMessage> _, ChatOptions? options, string? _, string? requestId, string? _, CancellationToken _) =>
            {
                capturedOptions = options;
                capturedRequestId = requestId;
            })
            .Returns(Task.FromResult(responseEntry));

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var typedResponse = await sessionClient.SendStructuredAsync<StructuredPayload>(
            "conversation-1",
            "Return a structured answer.",
            serializerOptions: serializerOptions,
            requestId: "structured-request");

        Assert.Equal("42", typedResponse.Result.Answer);
        Assert.Equal("provider-response", typedResponse.ResponseId);
        Assert.Equal("provider-conversation", typedResponse.ConversationId);
        Assert.Equal("model-1", typedResponse.ModelId);
        Assert.Equal(ChatFinishReason.Stop, typedResponse.FinishReason);
        Assert.Equal(12, typedResponse.Usage?.InputTokenCount);
        Assert.Equal("trace-1", Assert.IsType<JsonElement>(
            typedResponse.AdditionalProperties!["provider.trace"]).GetString());

        var format = Assert.IsType<ChatResponseFormatJson>(capturedOptions?.ResponseFormat);
        Assert.NotNull(format.Schema);
        Assert.Equal("object", format.Schema.Value.GetProperty("type").GetString());
        Assert.True(format.Schema.Value.GetProperty("properties").TryGetProperty("answer", out _));
        Assert.Equal("structured-request", capturedRequestId);
    }

    [Fact]
    public async Task SendStructuredAsync_RejectsCallerResponseFormatBeforeDispatch()
    {
        var sessionClient = A.Fake<IDurableChatSessionClient>();

        var exception = await Assert.ThrowsAsync<DurableConfigurationException>(() =>
            sessionClient.SendStructuredAsync<StructuredPayload>(
                "conversation-1",
                "Return a structured answer.",
                options: new ChatOptions { ResponseFormat = ChatResponseFormat.Text }));

        Assert.Contains(nameof(ChatOptions.ResponseFormat), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendStructuredAsync_RejectsNonObjectSchemaBeforeDispatch()
    {
        var sessionClient = A.Fake<IDurableChatSessionClient>();

        var exception = await Assert.ThrowsAsync<DurableConfigurationException>(() =>
            sessionClient.SendStructuredAsync<int>(
                "conversation-1",
                "Return a number."));

        Assert.Contains("JSON object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSchemaResponseFormat_RoundTripsThroughDurableConverter()
    {
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "return a structured value")],
            Options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<StructuredPayload>(),
            },
        };
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var restored = (DurableChatInput)converter.ToValue(
            converter.ToPayload(input),
            typeof(DurableChatInput))!;

        var format = Assert.IsType<ChatResponseFormatJson>(restored.Options?.ResponseFormat);
        Assert.NotNull(format.Schema);
        Assert.Equal("object", format.Schema.Value.GetProperty("type").GetString());
        Assert.True(format.Schema.Value.GetProperty("properties").TryGetProperty("answer", out _));
    }

    [Fact]
    public async Task SendStructuredAsync_MapsInvalidJsonToStableFailureAndRetainsResponse()
    {
        var responseEntry = CreateResponseEntry("not json");
        var sessionClient = CreateClient(responseEntry);

        var exception = await Assert.ThrowsAsync<DurableStructuredOutputException>(() =>
            sessionClient.SendStructuredAsync<StructuredPayload>(
                "conversation-1",
                "Return a structured answer."));

        Assert.Equal(DurableStructuredOutputFailureReason.InvalidResponse, exception.FailureReason);
        Assert.Same(responseEntry, exception.Response);
        Assert.Equal("provider-response", exception.Response.ResponseId);
    }

    [Fact]
    public async Task SendStructuredAsync_RejectsNonFinalResponseBeforeDeserialization()
    {
        var responseEntry = CreateResponseEntry(
            """{"answer":"42"}""",
            DurableTurnCompletionReason.IncompleteResponse);
        var sessionClient = CreateClient(responseEntry);

        var exception = await Assert.ThrowsAsync<DurableStructuredOutputException>(() =>
            sessionClient.SendStructuredAsync<StructuredPayload>(
                "conversation-1",
                "Return a structured answer."));

        Assert.Equal(DurableStructuredOutputFailureReason.IncompleteResponse, exception.FailureReason);
        Assert.Same(responseEntry, exception.Response);
    }

    private static IDurableChatSessionClient CreateClient(DurableSessionResponse responseEntry)
    {
        var sessionClient = A.Fake<IDurableChatSessionClient>();
        A.CallTo(() => sessionClient.SendAsync(
                A<string>._,
                A<IEnumerable<ChatMessage>>._,
                A<ChatOptions?>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(responseEntry));
        return sessionClient;
    }

    private static DurableSessionResponse CreateResponseEntry(
        string json,
        DurableTurnCompletionReason completionReason = DurableTurnCompletionReason.FinalResponse) =>
        DurableSessionResponse.FromChatResponse(
            "correlation-1",
            new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
            {
                ResponseId = "provider-response",
                ConversationId = "provider-conversation",
                ModelId = "model-1",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 12 },
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["provider.trace"] = "trace-1",
                },
            },
            DateTimeOffset.UnixEpoch,
            completionReason);

    public sealed class StructuredPayload
    {
        public string Answer { get; set; } = string.Empty;
    }
}
