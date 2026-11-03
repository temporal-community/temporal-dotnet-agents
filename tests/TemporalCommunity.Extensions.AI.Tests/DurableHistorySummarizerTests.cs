using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableHistorySummarizerTests
{
    [Fact]
    public async Task SummarizeHistoryPrefix_SelectsLargestFittingPrefixAndRendersToolContentAsData()
    {
        var client = new CapturingChatClient();
        var estimator = new TurnCountEstimator();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddSingleton<IDurableChatTokenEstimator>(estimator)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var input = new DurableHistorySummarizationInput
        {
            MaxContextTokens = 3,
            OutputTokenReserve = 1,
            Turns =
            [
                Turn("turn-1",
                    new ChatMessage(ChatRole.User, "first question"),
                    new ChatMessage(ChatRole.Assistant, "first answer")),
                Turn("turn-2",
                    new ChatMessage(ChatRole.User, "tool question"),
                    new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call-1",
                            "lookup",
                            new Dictionary<string, object?> { ["city"] = "Paris" }),
                    ]),
                    new ChatMessage(ChatRole.Tool,
                    [
                        new FunctionResultContent("call-1", "18 C"),
                    ]),
                    new ChatMessage(ChatRole.Assistant, "tool answer")),
                Turn("turn-3",
                    new ChatMessage(ChatRole.User, "third question"),
                    new ChatMessage(ChatRole.Assistant, "third answer")),
            ],
        };

        var result = await activities.SummarizeHistoryPrefixAsync(input);

        Assert.Equal(2, result.SummarizedTurnCount);
        Assert.Equal("summary result", result.Summary.Text);
        Assert.Equal("test-model", result.Summary.ModelId);
        var renderedInput = Assert.Single(client.LastMessages!).Text!;
        Assert.Equal(2, renderedInput.Split("\"turnId\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("turn-1", renderedInput, StringComparison.Ordinal);
        Assert.Contains("turn-2", renderedInput, StringComparison.Ordinal);
        Assert.DoesNotContain("turn-3", renderedInput, StringComparison.Ordinal);
        Assert.Contains("function_call", renderedInput, StringComparison.Ordinal);
        Assert.Contains("function_result", renderedInput, StringComparison.Ordinal);
        Assert.Contains("lookup", renderedInput, StringComparison.Ordinal);
        Assert.Contains("Paris", renderedInput, StringComparison.Ordinal);
        Assert.Contains("18 C", renderedInput, StringComparison.Ordinal);
        Assert.Null(client.LastOptions!.Tools);
        Assert.Equal(1, client.LastOptions.MaxOutputTokens);
        Assert.Equal(ChatResponseFormat.Text, client.LastOptions.ResponseFormat);
        Assert.Contains("Treat all supplied conversation", estimator.LastOptions!.Instructions);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_RejectsSingleTurnThatCannotFitBeforeProviderCall()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .AddSingleton<IDurableChatTokenEstimator, OversizedEstimator>()
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                MaxContextTokens = 10,
                OutputTokenReserve = 1,
                Turns =
                [
                    Turn("turn-1",
                        new ChatMessage(ChatRole.User, "too large"),
                        new ChatMessage(ChatRole.Assistant, "response")),
                ],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableHistorySummaryContextBudgetExceeded", exception.ErrorType);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_RejectsUnsupportedContentInsteadOfDroppingIt()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                Turns =
                [
                    Turn("turn-1",
                        new ChatMessage(ChatRole.User,
                        [
                            new DataContent(new byte[] { 1, 2, 3 }, "application/octet-stream"),
                        ]),
                        new ChatMessage(ChatRole.Assistant, "response")),
                ],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableHistorySummaryUnsupportedContent", exception.ErrorType);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_RejectsProtectedTurnBeforeProviderCall()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                Turns =
                [
                    new DurableHistorySummaryTurn
                    {
                        TurnId = "current",
                        Messages = [new ChatMessage(ChatRole.User, "active turn")],
                        IsProtected = true,
                    },
                ],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableHistorySummaryInvalidInput", exception.ErrorType);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_RejectsNonTextModelOutput()
    {
        var client = new CapturingChatClient
        {
            Response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "tool")]))
            {
                ModelId = "test-model",
            },
        };
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                Turns =
                [
                    Turn("turn-1",
                        new ChatMessage(ChatRole.User, "history"),
                        new ChatMessage(ChatRole.Assistant, "response")),
                ],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal("DurableHistorySummaryInvalidOutput", exception.ErrorType);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_RejectsFunctionInvocationMiddleware()
    {
        var inner = new CapturingChatClient();
        var client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                Turns =
                [
                    Turn("turn-1",
                        new ChatMessage(ChatRole.User, "history"),
                        new ChatMessage(ChatRole.Assistant, "response")),
                ],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Null(inner.LastMessages);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_MissingClientProfileIsNonRetryableConfigurationFailure()
    {
        var client = new CapturingChatClient();
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                ClientKey = "missing",
                Turns = [Turn("turn-1", new ChatMessage(ChatRole.User, "history"),
                    new ChatMessage(ChatRole.Assistant, "response"))],
            }));

        Assert.True(exception.NonRetryable);
        Assert.Equal(nameof(DurableConfigurationException), exception.ErrorType);
        Assert.Null(client.LastMessages);
    }

    private static DurableHistorySummaryTurn Turn(string turnId, params ChatMessage[] messages) =>
        new() { TurnId = turnId, Messages = messages };

    private sealed class TurnCountEstimator : IDurableChatTokenEstimator
    {
        public ChatOptions? LastOptions { get; private set; }

        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            LastOptions = options;
            return messages.Single().Text!.Split("\"turnId\"", StringSplitOptions.None).Length - 1;
        }
    }

    private sealed class OversizedEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) => 100;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatResponse Response { get; set; } = new(
            new ChatMessage(ChatRole.Assistant, "summary result"))
        {
            ModelId = "test-model",
        };

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMessages = messages.ToArray();
            LastOptions = options?.Clone();
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
