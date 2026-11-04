#pragma warning disable MEAI001 // The pinned MEAI reducer interface is experimental.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableChatActivitiesHeartbeatTests
{
    [Fact]
    public async Task SummarizeHistoryPrefix_HeartbeatsDuringBufferedProviderCall()
    {
        var client = new DelayedBufferedChatClient(TimeSpan.FromMilliseconds(300));
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var heartbeatCount = 0;
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            },
            Heartbeater = _ => Interlocked.Increment(ref heartbeatCount),
        };

        var result = await environment.RunAsync(() =>
            activities.SummarizeHistoryPrefixAsync(new DurableHistorySummarizationInput
            {
                Turns =
                [
                    new DurableHistorySummaryTurn
                    {
                        TurnId = "turn-1",
                        Messages =
                        [
                            new ChatMessage(ChatRole.User, "prior question"),
                            new ChatMessage(ChatRole.Assistant, "prior answer"),
                        ],
                    },
                ],
            }));

        Assert.Equal("summary result", result.Summary.Text);
        Assert.Equal(1, client.ResponseCallCount);
        Assert.True(Volatile.Read(ref heartbeatCount) >= 2);
        Assert.False(client.ReceivedCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SummarizeHistoryPrefix_PropagatesCancellationDuringBufferedProviderCall()
    {
        var client = new DelayedBufferedChatClient(Timeout.InfiniteTimeSpan);
        using var services = new ServiceCollection()
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var heartbeatCount = 0;
        ActivityEnvironment? environment = null;
        environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            },
            Heartbeater = _ =>
            {
                if (client.Started.Task.IsCompleted
                    && Interlocked.Increment(ref heartbeatCount) == 2)
                {
                    environment!.Cancel();
                }
            },
        };

        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                environment.RunAsync(() => activities.SummarizeHistoryPrefixAsync(
                    new DurableHistorySummarizationInput
                    {
                        Turns =
                        [
                            new DurableHistorySummaryTurn
                            {
                                TurnId = "turn-1",
                                Messages =
                                [
                                    new ChatMessage(ChatRole.User, "prior question"),
                                    new ChatMessage(ChatRole.Assistant, "prior answer"),
                                ],
                            },
                        ],
                    })));

            Assert.True(client.Started.Task.IsCompletedSuccessfully);
            Assert.Equal(1, client.ResponseCallCount);
            Assert.True(Volatile.Read(ref heartbeatCount) >= 2);
            Assert.True(client.ReceivedCancellationToken.IsCancellationRequested);
            Assert.Equal(environment.CancellationTokenSource.Token, client.ReceivedCancellationToken);
            Assert.Equal(client.ReceivedCancellationToken, exception.CancellationToken);
        }
        finally
        {
            environment.Cancel();
        }
    }

    [Fact]
    public async Task GetChatStepAsync_HeartbeatsDuringAsyncReducerAndPropagatesCancellation()
    {
        var reducer = new WaitingReducer();
        var client = new CountingStreamingChatClient();
        using var services = new ServiceCollection()
            .AddKeyedSingleton<IChatReducer>("async", reducer)
            .AddSingleton<IChatClient>(client)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, loggerFactory: null);
        var heartbeatCount = 0;
        ActivityEnvironment? environment = null;
        environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            },
            Heartbeater = _ =>
            {
                if (reducer.Started.Task.IsCompleted
                    && Interlocked.Increment(ref heartbeatCount) == 2)
                {
                    environment!.Cancel();
                }
            },
        };
        var input = new DurableChatInput
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, "prior question"),
                new ChatMessage(ChatRole.Assistant, "prior answer"),
                new ChatMessage(ChatRole.User, "current question"),
            ],
            ContextReducerKey = "async",
            CurrentTurnStartIndex = 2,
            PreviousTurnRanges = [new DurableChatTurnRange(0, 2, IsProtected: false)],
        };

        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => environment.RunAsync(() => activities.GetChatStepAsync(input)));

            Assert.True(reducer.Started.Task.IsCompletedSuccessfully);
            Assert.True(Volatile.Read(ref heartbeatCount) >= 2);
            Assert.True(reducer.ReceivedCancellationToken.IsCancellationRequested);
            Assert.Equal(environment.CancellationTokenSource.Token, reducer.ReceivedCancellationToken);
            Assert.Equal(reducer.ReceivedCancellationToken, exception.CancellationToken);
            Assert.Equal(0, client.StreamingCallCount);
        }
        finally
        {
            environment.Cancel();
        }
    }

    private sealed class DelayedBufferedChatClient(TimeSpan delay) : IChatClient
    {
        private int _responseCallCount;
        private CancellationToken _receivedCancellationToken;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResponseCallCount => Volatile.Read(ref _responseCallCount);
        public CancellationToken ReceivedCancellationToken => _receivedCancellationToken;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _responseCallCount);
            _receivedCancellationToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary result"));
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

    private sealed class WaitingReducer : IChatReducer
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public async Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return messages;
        }
    }

    private sealed class CountingStreamingChatClient : IChatClient
    {
        private int _streamingCallCount;

        public int StreamingCallCount => Volatile.Read(ref _streamingCallCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unexpected")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _streamingCallCount);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unexpected");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
