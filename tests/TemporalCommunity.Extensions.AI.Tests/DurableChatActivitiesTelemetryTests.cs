using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Verifies that <see cref="DurableChatActivities"/> populates GenAI usage tags
/// (input_tokens / output_tokens / response.model) on the "chat {modelId}" span.
/// Regression for Morpheus's unverified item B2 from the OTel sample review.
/// </summary>
[Collection(nameof(DurableToolsetTelemetryTests))]
public class DurableChatActivitiesTelemetryTests
{
    private const int InputTokens = 42;
    private const int OutputTokens = 17;
    private const int ReasoningTokens = 9;
    private const int MaxOutputTokens = 321;
    private const string RequestModel = "test-model-req";
    private const string ResponseModel = "test-model-resp";

    private static DurableChatActivities BuildActivities(
        IChatClient client,
        ILoggerFactory? loggerFactory = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();
        return new DurableChatActivities(provider, loggerFactory);
    }

    // AsyncLocal so concurrent test classes that emit on the same ActivitySource
    // (DurableChatActivitiesDecorationTests, ...StreamingTests, etc.) don't pollute
    // this test's captured list. xunit assigns each [Fact] its own async context;
    // ActivityStopped fires on whichever context closed the activity — for awaited
    // GetResponseAsync / GetChatStepAsync that is this test's flow.
    private static readonly AsyncLocal<List<Activity>?> CurrentCaptured = new();

    // One process-wide listener is enough — installing per-test races with other
    // tests' listeners and is the original source of the flake. The listener routes
    // every span to the AsyncLocal of whichever test (if any) is currently capturing.
    static DurableChatActivitiesTelemetryTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name == DurableChatTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                var bag = CurrentCaptured.Value;
                if (bag is null)
                {
                    return;
                }
                lock (bag)
                {
                    bag.Add(a);
                }
            },
        });
    }

    private static List<Activity> StartCapture()
    {
        var captured = new List<Activity>();
        CurrentCaptured.Value = captured;
        return captured;
    }

    private static DurableChatInput InputWithModel() => new()
    {
        ConversationId = "conv-test-1",
        TurnNumber = 1,
        Messages = [new ChatMessage(ChatRole.User, "ping")],
        Options = new ChatOptions
        {
            ModelId = RequestModel,
            MaxOutputTokens = MaxOutputTokens,
        },
    };

    [Fact]
    public async Task GetResponseAsync_PopulatesUsageAndModelTagsOnChatSpan()
    {
        var captured = StartCapture();

        var client = new UsageReportingChatClient();
        var activities = BuildActivities(client);

        var response = await activities.GetResponseAsync(InputWithModel());

        // Source data was actually produced.
        Assert.NotNull(response.Usage);
        Assert.Equal(InputTokens, response.Usage!.InputTokenCount);
        Assert.Equal(OutputTokens, response.Usage.OutputTokenCount);
        Assert.Equal(ResponseModel, response.ModelId);

        var span = Assert.Single(captured);
        Assert.Equal($"chat {RequestModel}", span.DisplayName);

        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.True(tags.ContainsKey(DurableChatTelemetry.InputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.InputTokensAttribute}");
        Assert.True(tags.ContainsKey(DurableChatTelemetry.OutputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.OutputTokensAttribute}");

        Assert.Equal((long)InputTokens, Convert.ToInt64(tags[DurableChatTelemetry.InputTokensAttribute]));
        Assert.Equal((long)OutputTokens, Convert.ToInt64(tags[DurableChatTelemetry.OutputTokensAttribute]));
        Assert.Equal(ResponseModel, tags[DurableChatTelemetry.ResponseModelAttribute]);
        Assert.Equal(RequestModel, tags[DurableChatTelemetry.RequestModelAttribute]);
        Assert.Equal(DurableChatTelemetry.ChatOperationName, tags[DurableChatTelemetry.OperationNameAttribute]);
        Assert.Equal("conv-test-1", tags[DurableChatTelemetry.ConversationIdAttribute]);
    }

    [Fact]
    public async Task GetChatStepAsync_PopulatesUsageFinishAndRequestTagsOnChatSpan()
    {
        var captured = StartCapture();

        var client = new UsageReportingChatClient();
        var activities = BuildActivities(client);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.NotNull(result.Usage);
        Assert.Equal(InputTokens, result.Usage!.InputTokenCount);
        Assert.Equal(OutputTokens, result.Usage.OutputTokenCount);
        Assert.Equal(ReasoningTokens, result.Usage.ReasoningTokenCount);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);

        var span = Assert.Single(captured);
        Assert.Equal($"chat {RequestModel}", span.DisplayName);

        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.True(tags.ContainsKey(DurableChatTelemetry.InputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.InputTokensAttribute} on Pattern 3 path");
        Assert.True(tags.ContainsKey(DurableChatTelemetry.OutputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.OutputTokensAttribute} on Pattern 3 path");

        Assert.Equal((long)InputTokens, Convert.ToInt64(tags[DurableChatTelemetry.InputTokensAttribute]));
        Assert.Equal((long)OutputTokens, Convert.ToInt64(tags[DurableChatTelemetry.OutputTokensAttribute]));
        Assert.Equal(
            (long)ReasoningTokens,
            Convert.ToInt64(tags[DurableChatTelemetry.ReasoningOutputTokensAttribute]));
        Assert.Equal(
            MaxOutputTokens,
            Convert.ToInt32(tags[DurableChatTelemetry.RequestMaxTokensAttribute]));
        Assert.Equal(
            [ChatFinishReason.Stop.Value],
            Assert.IsType<string[]>(tags[DurableChatTelemetry.ResponseFinishReasonsAttribute]));
        Assert.Equal(ResponseModel, tags[DurableChatTelemetry.ResponseModelAttribute]);
        Assert.Equal(false, tags[DurableChatTelemetry.EmptyVisibleTextAttribute]);
        Assert.Equal(
            DurableTurnCompletionReason.FinalResponse.ToString(),
            tags[DurableChatTelemetry.TurnCompletionReasonAttribute]);
    }

    [Fact]
    public async Task GetChatStepAsync_EmptyIncompleteResponse_SetsSpanAttributeAndStructuredWarning()
    {
        var captured = StartCapture();

        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        using var client = new EmptyLengthChatClient();
        var activities = BuildActivities(client, loggerFactory);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.Equal(DurableTurnCompletionReason.IncompleteResponse, result.CompletionReason);
        var span = Assert.Single(captured);
        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(true, tags[DurableChatTelemetry.EmptyVisibleTextAttribute]);
        Assert.Equal(
            DurableTurnCompletionReason.IncompleteResponse.ToString(),
            tags[DurableChatTelemetry.TurnCompletionReasonAttribute]);

        var warning = Assert.Single(logs.Logs, log => log.EventId.Id == 26);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("FinishReason=length", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ToolCalls=0", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Contradictory=False", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChatStepAsync_ContinueWithTools_OmitsCompletionReasonFromChatSpan()
    {
        var captured = StartCapture();

        using var client = new ToolCallChatClient();
        var activities = BuildActivities(client);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.False(result.IsFinal);
        Assert.Single(result.ToolCalls!);
        var span = Assert.Single(captured);
        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.DoesNotContain(DurableChatTelemetry.TurnCompletionReasonAttribute, tags.Keys);
    }

    /// <summary>
    /// Streaming chat client that emits a text chunk plus a terminal <see cref="UsageContent"/>
    /// update so <c>ToChatResponse()</c> populates <see cref="ChatResponse.Usage"/>.
    /// </summary>
    private sealed class UsageReportingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("activities use streaming");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello")
            {
                ModelId = ResponseModel,
            };
            var usage = new UsageDetails
            {
                InputTokenCount = InputTokens,
                OutputTokenCount = OutputTokens,
                ReasoningTokenCount = ReasoningTokens,
            };
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = ResponseModel,
                Contents = [new UsageContent(usage)],
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class EmptyLengthChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("activities use streaming");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [],
                FinishReason = ChatFinishReason.Length,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolCallChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("activities use streaming");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent(
                        "continue-call",
                        "test_tool",
                        new Dictionary<string, object?>()),
                ],
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
