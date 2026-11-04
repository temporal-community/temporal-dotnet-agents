#pragma warning disable MEAI001 // The pinned MEAI reducer interface is experimental.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class DurableChatActivityHeartbeatIntegrationTests
{
    [Theory]
    [InlineData(true, "TemporalCommunity.Extensions.AI.SummarizeHistoryPrefix")]
    [InlineData(false, "TemporalCommunity.Extensions.AI.GetChatStep")]
    public async Task HealthyPreparationOutlivesHeartbeatTimeout_WithoutRetries_AndReplays(
        bool summarize,
        string activityType)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var client = new DelayedChatClient();
        var reducer = new DelayedReducer();
        var taskQueue = $"chat-heartbeats-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services.AddSingleton<IChatClient>(client);
        builder.Services.AddKeyedSingleton<IChatReducer>("delayed", reducer);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<HeartbeatProbeWorkflow>()
            .AddSingletonActivities<DurableChatActivities>();
        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (HeartbeatProbeWorkflow workflow) => workflow.RunAsync(summarize),
                new WorkflowOptions($"chat-heartbeats-{Guid.NewGuid():N}", taskQueue));

            Assert.Equal(
                summarize ? "buffered summary" : "model response",
                await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, Assert.Single(client.Attempts));
            Assert.Equal(summarize ? 1 : 0, client.BufferedCalls);
            Assert.Equal(summarize ? 0 : 1, client.StreamingCalls);
            if (summarize)
            {
                Assert.True(client.BufferedElapsed >= TimeSpan.FromMilliseconds(1200));
                Assert.Empty(reducer.Attempts);
            }
            else
            {
                Assert.Equal(1, Assert.Single(reducer.Attempts));
                Assert.True(reducer.Elapsed >= TimeSpan.FromMilliseconds(1200));
            }

            var history = await handle.FetchHistoryAsync();
            var scheduled = Assert.Single(history.Events,
                ev => ev.ActivityTaskScheduledEventAttributes is not null)
                .ActivityTaskScheduledEventAttributes;
            Assert.Equal(activityType, scheduled.ActivityType.Name);
            Assert.Equal(
                TimeSpan.FromMilliseconds(300),
                scheduled.HeartbeatTimeout.ToTimeSpan());
            Assert.Equal(
                TimeSpan.FromSeconds(10),
                scheduled.StartToCloseTimeout.ToTimeSpan());
            var started = Assert.Single(history.Events,
                ev => ev.ActivityTaskStartedEventAttributes is not null)
                .ActivityTaskStartedEventAttributes;
            Assert.Equal(1, started.Attempt);
            Assert.Null(started.LastFailure);
            Assert.Single(history.Events, ev => ev.ActivityTaskCompletedEventAttributes is not null);
            Assert.DoesNotContain(history.Events, ev =>
                ev.ActivityTaskTimedOutEventAttributes is not null
                || ev.ActivityTaskFailedEventAttributes is not null
                || ev.ActivityTaskCanceledEventAttributes is not null);

            var replayOptions = new WorkflowReplayerOptions
            {
                DataConverter = DurableAIDataConverter.Instance,
            };
            replayOptions.AddWorkflow<HeartbeatProbeWorkflow>();
            var replayer = new WorkflowReplayer(replayOptions);
            Assert.Null((await replayer.ReplayWorkflowAsync(
                history, throwOnReplayFailure: false)).ReplayFailure);
            Assert.Single(client.Attempts);
            Assert.Equal(summarize ? 0 : 1, reducer.Attempts.Count);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("TemporalCommunity.Extensions.AI.Tests.HeartbeatProbe")]
    public sealed class HeartbeatProbeWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(bool summarize)
        {
            var options = new ActivityOptions
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(300),
                StartToCloseTimeout = TimeSpan.FromSeconds(10),
                RetryPolicy = new RetryPolicy
                {
                    MaximumAttempts = 3,
                    InitialInterval = TimeSpan.FromMilliseconds(50),
                },
            };
            if (summarize)
            {
                var input = new DurableHistorySummarizationInput
                {
                    Turns =
                    [
                        new DurableHistorySummaryTurn
                        {
                            TurnId = "prior-turn",
                            Messages =
                            [
                                new ChatMessage(ChatRole.User, "prior question"),
                                new ChatMessage(ChatRole.Assistant, "prior answer"),
                            ],
                        },
                    ],
                };
                var summary = await Workflow.ExecuteActivityAsync(
                    (DurableChatActivities activities) => activities.SummarizeHistoryPrefixAsync(input),
                    options);
                return summary.Summary.Text;
            }

            var chatInput = new DurableChatInput
            {
                Messages =
                [
                    new ChatMessage(ChatRole.User, "prior question"),
                    new ChatMessage(ChatRole.Assistant, "prior answer"),
                    new ChatMessage(ChatRole.User, "current question"),
                ],
                ContextReducerKey = "delayed",
                CurrentTurnStartIndex = 2,
                PreviousTurnRanges = [new DurableChatTurnRange(0, 2, IsProtected: false)],
            };
            var response = await Workflow.ExecuteActivityAsync(
                (DurableChatActivities activities) => activities.GetChatStepAsync(chatInput),
                options);
            return response.Messages.Single().Text!;
        }
    }

    private sealed class DelayedChatClient : IChatClient
    {
        public ConcurrentQueue<int> Attempts { get; } = new();
        public int BufferedCalls { get; private set; }
        public int StreamingCalls { get; private set; }
        public TimeSpan BufferedElapsed { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(ActivityExecutionContext.Current.Info.Attempt);
            BufferedCalls++;
            var timer = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
            BufferedElapsed = timer.Elapsed;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "buffered summary"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(ActivityExecutionContext.Current.Info.Attempt);
            StreamingCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "model response")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DelayedReducer : IChatReducer
    {
        public ConcurrentQueue<int> Attempts { get; } = new();
        public TimeSpan Elapsed { get; private set; }

        public async Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(ActivityExecutionContext.Current.Info.Attempt);
            var timer = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
            Elapsed = timer.Elapsed;
            return messages;
        }
    }
}
