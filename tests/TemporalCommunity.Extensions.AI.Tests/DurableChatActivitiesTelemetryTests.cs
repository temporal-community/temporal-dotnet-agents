using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Verifies that <see cref="DurableChatActivities"/> populates GenAI usage tags
/// (input_tokens / output_tokens / response.model) on the "chat {modelId}" span.
/// Regression for Morpheus's unverified item B2 from the OTel sample review.
/// </summary>
public class DurableChatActivitiesTelemetryTests
{
    private const int InputTokens = 42;
    private const int OutputTokens = 17;
    private const string RequestModel = "test-model-req";
    private const string ResponseModel = "test-model-resp";

    private static DurableChatActivities BuildActivities(IChatClient client)
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();
        return new DurableChatActivities(provider, loggerFactory: null);
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
        Options = new ChatOptions { ModelId = RequestModel },
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
    public async Task GetChatStepAsync_PopulatesUsageAndModelTagsOnChatSpan()
    {
        var captured = StartCapture();

        var client = new UsageReportingChatClient();
        var activities = BuildActivities(client);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.NotNull(result.Usage);
        Assert.Equal(InputTokens, result.Usage!.InputTokenCount);
        Assert.Equal(OutputTokens, result.Usage.OutputTokenCount);

        var span = Assert.Single(captured);
        Assert.Equal($"chat {RequestModel}", span.DisplayName);

        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.True(tags.ContainsKey(DurableChatTelemetry.InputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.InputTokensAttribute} on Pattern 3 path");
        Assert.True(tags.ContainsKey(DurableChatTelemetry.OutputTokensAttribute),
            $"Missing tag {DurableChatTelemetry.OutputTokensAttribute} on Pattern 3 path");

        Assert.Equal((long)InputTokens, Convert.ToInt64(tags[DurableChatTelemetry.InputTokensAttribute]));
        Assert.Equal((long)OutputTokens, Convert.ToInt64(tags[DurableChatTelemetry.OutputTokensAttribute]));
        Assert.Equal(ResponseModel, tags[DurableChatTelemetry.ResponseModelAttribute]);
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
            };
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = ResponseModel,
                Contents = [new UsageContent(usage)],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
