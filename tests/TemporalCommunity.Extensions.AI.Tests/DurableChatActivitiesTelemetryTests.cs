using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

/// <summary>
/// Verifies that GenAI inference telemetry is emitted only by an application-configured MEAI
/// <see cref="OpenTelemetryChatClient"/>, never by package-authored inference spans.
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
        ILoggerFactory? loggerFactory = null,
        DurableInferenceTelemetryMode telemetryMode = DurableInferenceTelemetryMode.ApplicationOwned)
    {
        var services = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton(new DurableExecutionOptions
            {
                InferenceTelemetryMode = telemetryMode,
            });
        var provider = services.BuildServiceProvider();
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
            ShouldListenTo = _ => true,
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
    public async Task GetResponseAsync_LeavesInferenceInstrumentationToApplication()
    {
        var captured = StartCapture();

        var client = new UsageReportingChatClient();
        var activities = BuildActivities(client);

        var response = await activities.GetResponseAsync(InputWithModel());

        Assert.NotNull(response.Usage);
        Assert.Equal(InputTokens, response.Usage!.InputTokenCount);
        Assert.Equal(OutputTokens, response.Usage.OutputTokenCount);
        Assert.Equal(ResponseModel, response.ModelId);
        Assert.DoesNotContain(captured, IsInferenceSpan);
    }

    [Fact]
    public async Task GetChatStepAsync_ApplicationConfiguredMEAIWrapperEmitsInferenceSpan()
    {
        var captured = StartCapture();

        var client = new ChatClientBuilder(new UsageReportingChatClient())
            .UseOpenTelemetry()
            .Build();
        var activities = BuildActivities(
            client,
            telemetryMode: DurableInferenceTelemetryMode.RequireVisibleOpenTelemetryClient);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.NotNull(result.Usage);
        Assert.Equal(InputTokens, result.Usage!.InputTokenCount);
        Assert.Equal(OutputTokens, result.Usage.OutputTokenCount);
        Assert.Equal(ReasoningTokens, result.Usage.ReasoningTokenCount);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);

        var span = Assert.Single(captured, IsInferenceSpan);
        var tags = span.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("chat", tags["gen_ai.operation.name"]);
        Assert.Equal(RequestModel, tags["gen_ai.request.model"]);
        Assert.Equal(ResponseModel, tags["gen_ai.response.model"]);
        Assert.Equal((long)InputTokens, Convert.ToInt64(tags["gen_ai.usage.input_tokens"]));
        Assert.Equal((long)OutputTokens, Convert.ToInt64(tags["gen_ai.usage.output_tokens"]));
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
        Assert.DoesNotContain(captured, IsInferenceSpan);

        var warning = Assert.Single(logs.Logs, log => log.EventId.Id == 26);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("FinishReason=length", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ToolCalls=0", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Contradictory=False", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChatStepAsync_WithoutMEAIWrapperEmitsNoInferenceSpan()
    {
        var captured = StartCapture();

        using var client = new ToolCallChatClient();
        var activities = BuildActivities(client);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.False(result.IsFinal);
        Assert.Single(result.ToolCalls!);
        Assert.DoesNotContain(captured, IsInferenceSpan);
    }

    [Fact]
    public async Task OpaqueApplicationChain_RequiresExplicitTelemetryTrust()
    {
        var captured = StartCapture();
        var wrapped = new ChatClientBuilder(new UsageReportingChatClient()).UseOpenTelemetry().Build();
        var opaque = new OpaqueChatClient(wrapped);
        var activities = BuildActivities(
            opaque,
            telemetryMode: DurableInferenceTelemetryMode.TrustOpaqueApplicationInstrumentation);

        var result = await activities.GetChatStepAsync(InputWithModel());

        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);
        Assert.Single(captured, IsInferenceSpan);
    }

    [Fact]
    public async Task RequireVisibleTelemetryMode_RejectsMissingMEAIWrapper()
    {
        var activities = BuildActivities(
            new UsageReportingChatClient(),
            telemetryMode: DurableInferenceTelemetryMode.RequireVisibleOpenTelemetryClient);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => activities.GetChatStepAsync(InputWithModel()));

        Assert.Equal("DurableInferenceTelemetryConfigurationInvalid", exception.ErrorType);
    }

    private static bool IsInferenceSpan(Activity activity) =>
        activity.GetTagItem("gen_ai.operation.name") is string;

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

    private sealed class OpaqueChatClient(IChatClient inner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

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
