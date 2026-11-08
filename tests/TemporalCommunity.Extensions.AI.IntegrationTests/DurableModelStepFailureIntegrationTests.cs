#pragma warning disable MEAI001 // IChatReducer is experimental in the pinned MEAI version.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class DurableModelStepFailureIntegrationTests
{
    private const string ModelActivity = "TemporalCommunity.Extensions.AI.GetChatStep";
    private const string Secret = "synthetic-private-exception-content";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("budget", "DurableContextBudgetExceeded", false)]
    [InlineData("missing-reducer", "DurableContextReducerNotFound", false)]
    [InlineData("invalid-reducer", "DurableContextReducerInvalidOutput", true)]
    [InlineData("permanent-reducer", "CustomReducerPermanent", true)]
    public async Task PermanentContextFailure_PreservesIdentityAndSchedulesOnlyOneActivity(
        string scenario,
        string expectedErrorType,
        bool needsPriorTurn)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingChatClient();
        var reducer = new FailingReducer(scenario);
        var taskQueue = $"context-failure-{Guid.NewGuid():N}";
        using var host = BuildHost(
            env.Client,
            taskQueue,
            model,
            options =>
            {
                if (scenario == "budget")
                {
                    options.MaxModelContextTokens = 32;
                    options.ModelOutputTokenReserve = 31;
                }
                else
                {
                    options.DefaultContextReducerKey = "context";
                }
            },
            scenario == "missing-reducer" ? null : reducer);
        await host.StartAsync();
        try
        {
            var session = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"context-{Guid.NewGuid():N}";
            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
            if (needsPriorTurn)
            {
                Assert.Equal("completed", (await session.SendAsync(
                    conversationId, "prior request", requestId: "prior").WaitAsync(Deadline)).Text);
                var priorOutcome = await session.GetTerminalTurnOutcomeAsync(
                    conversationId, "prior").WaitAsync(Deadline);
                Assert.NotNull(priorOutcome);
                Assert.Null(priorOutcome.FailureNonRetryable);
                Assert.Empty(reducer.Calls);
            }

            var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(
                    conversationId, "cannot prepare context", requestId: "failed").WaitAsync(Deadline));

            var applicationFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
            Assert.Equal(expectedErrorType, applicationFailure.ErrorType);
            Assert.True(applicationFailure.NonRetryable);
            Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
            var terminal = await session.GetTerminalTurnOutcomeAsync(
                conversationId, "failed").WaitAsync(Deadline);
            Assert.NotNull(terminal);
            Assert.Equal(expectedErrorType, terminal.FailureErrorType);
            Assert.Equal(true, terminal.FailureNonRetryable);

            Assert.Equal(needsPriorTurn ? 1 : 0, model.Calls.Count);
            Assert.Equal(needsPriorTurn ? 1 : 0, reducer.Calls.Count);
            Assert.All(reducer.Calls, info => Assert.Equal(1, info.Attempt));
            Assert.Equal(
                needsPriorTurn ? 2 : 1,
                await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, ModelActivity));

            var failedSteps = new List<Temporalio.Api.History.V1.ActivityTaskFailedEventAttributes>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskFailedEventAttributes is { } attributes)
                {
                    failedSteps.Add(attributes);
                }
            }
            var failedStep = Assert.Single(failedSteps);
            Assert.Equal(expectedErrorType, failedStep.Failure.ApplicationFailureInfo.Type);
            Assert.True(failedStep.Failure.ApplicationFailureInfo.NonRetryable);
            await CompleteAndReplayAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ClrNonRetryableErrorType_SuppressesBothActivityRetryAndManagedRescheduling()
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingChatClient(
            _ => new InvalidOperationException(Secret, new Exception(Secret)));
        var taskQueue = $"clr-retry-type-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model, options =>
            options.RetryPolicy = new RetryPolicy
            {
                MaximumAttempts = 3,
                InitialInterval = TimeSpan.FromMilliseconds(10),
                NonRetryableErrorTypes = [nameof(InvalidOperationException)],
            });
        await host.StartAsync();
        try
        {
            var session = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"clr-{Guid.NewGuid():N}";
            var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                session.SendAsync(conversationId, "request", requestId: "failed").WaitAsync(Deadline));

            var applicationFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
            Assert.Equal(nameof(InvalidOperationException), applicationFailure.ErrorType);
            Assert.True(applicationFailure.NonRetryable);
            Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(model.Calls).Attempt);
            var terminal = await session.GetTerminalTurnOutcomeAsync(
                conversationId, "failed").WaitAsync(Deadline);
            Assert.NotNull(terminal);
            Assert.Equal(nameof(InvalidOperationException), terminal.FailureErrorType);

            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
            Assert.Equal(1, await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, ModelActivity));
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskFailedEventAttributes is { } failed)
                {
                    Assert.Equal(nameof(InvalidOperationException), failed.Failure.ApplicationFailureInfo.Type);
                    // This is the server retry-policy exclusion, not an activity-side permanent flag.
                    Assert.False(failed.Failure.ApplicationFailureInfo.NonRetryable);
                    Assert.Equal(
                        Temporalio.Api.Enums.V1.RetryState.NonRetryableFailure,
                        failed.RetryState);
                    Assert.DoesNotContain(Secret, failed.ToString(), StringComparison.Ordinal);
                }
            }
            await CompleteAndReplayAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientFailure_RetriesSameScheduledActivityAndPreservesExplicitDelay(
        bool explicitDelay)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var retryDelay = TimeSpan.FromMilliseconds(500);
        var model = new RecordingChatClient(call => call > 2
            ? null
            : explicitDelay
                ? new ApplicationFailureException(
                    Secret,
                    new Exception(Secret),
                    errorType: "ProviderBackoff",
                    details: [Secret],
                    nextRetryDelay: retryDelay)
                : new InvalidOperationException(Secret));
        var taskQueue = $"transient-retry-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model);
        await host.StartAsync();
        try
        {
            var session = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"transient-{Guid.NewGuid():N}";
            var response = await session.SendAsync(
                conversationId, "retry transient failure").WaitAsync(Deadline);

            Assert.Equal("completed", response.Text);
            var attempts = model.Calls.ToArray();
            Assert.Equal([1, 2, 3], attempts.Select(info => info.Attempt));
            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
            Assert.Equal(1, await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, ModelActivity));
            var startedSteps = new List<Temporalio.Api.History.V1.ActivityTaskStartedEventAttributes>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskStartedEventAttributes is { LastFailure: not null } started)
                {
                    startedSteps.Add(started);
                }
            }
            await CompleteAndReplayAsync(handle);
            var lastAttempt = Assert.Single(startedSteps);
            Assert.Equal(3, lastAttempt.Attempt);
            var lastFailure = lastAttempt.LastFailure;
            Assert.Equal(
                explicitDelay ? "ProviderBackoff" : nameof(InvalidOperationException),
                lastFailure.ApplicationFailureInfo.Type);
            Assert.False(lastFailure.ApplicationFailureInfo.NonRetryable);
            Assert.Null(lastFailure.Cause);
            Assert.True(lastFailure.ApplicationFailureInfo.Details is null
                || lastFailure.ApplicationFailureInfo.Details.Payloads_.Count == 0);
            Assert.DoesNotContain(Secret, lastFailure.ToString(), StringComparison.Ordinal);
            if (explicitDelay)
            {
                Assert.Equal(retryDelay, lastFailure.ApplicationFailureInfo.NextRetryDelay.ToTimeSpan());
                for (var index = 1; index < attempts.Length; index++)
                {
                    // Server timestamps verify actual backoff, not elapsed test-runner time.
                    Assert.True(
                        attempts[index].CurrentAttemptScheduledTime - attempts[index - 1].StartedTime
                            >= retryDelay);
                }
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(1, false, 2)]
    [InlineData(3, true, 3)]
    public async Task RetryableActivityExhaustion_StillUsesManagedConsecutiveErrorAllowance(
        int failuresBeforeSuccess,
        bool failsTurn,
        int expectedSchedules)
    {
        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var model = new RecordingChatClient(call => call <= failuresBeforeSuccess
            ? new InvalidOperationException(Secret)
            : null);
        var taskQueue = $"managed-error-allowance-{Guid.NewGuid():N}";
        using var host = BuildHost(env.Client, taskQueue, model, options =>
            options.RetryPolicy = new RetryPolicy { MaximumAttempts = 1 });
        await host.StartAsync();
        try
        {
            var session = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"allowance-{Guid.NewGuid():N}";
            var turn = session.SendAsync(
                conversationId, "retry exhausted activity", requestId: "request").WaitAsync(Deadline);
            if (failsTurn)
            {
                var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => turn);
                var applicationFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
                Assert.Equal("LlmStepConsecutiveErrors", applicationFailure.ErrorType);
                var terminal = await session.GetTerminalTurnOutcomeAsync(
                    conversationId, "request").WaitAsync(Deadline);
                Assert.NotNull(terminal);
                Assert.Equal("LlmStepConsecutiveErrors", terminal.FailureErrorType);
            }
            else
            {
                Assert.Equal("completed", (await turn).Text);
            }

            Assert.Equal(expectedSchedules, model.Calls.Count);
            Assert.All(model.Calls, info => Assert.Equal(1, info.Attempt));
            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(session.GetWorkflowId(conversationId));
            Assert.Equal(
                expectedSchedules,
                await WorkflowHistoryAssertions.CountActivityScheduledAsync(handle, ModelActivity));
            await CompleteAndReplayAsync(handle);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task CompleteAndReplayAsync(WorkflowHandle<DurableChatWorkflow> handle)
    {
        await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync()).WaitAsync(Deadline);
        await handle.GetResultAsync().WaitAsync(Deadline);
        var options = new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<DurableChatWorkflow>();
        var replay = await new WorkflowReplayer(options).ReplayWorkflowAsync(
            await handle.FetchHistoryAsync(), throwOnReplayFailure: false);
        Assert.Null(replay.ReplayFailure);
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient model,
        Action<DurableExecutionOptions>? configure = null,
        IChatReducer? reducer = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddChatClient(model).Build();
        if (reducer is not null)
        {
            builder.Services.AddKeyedSingleton<IChatReducer>("context", reducer);
        }
        builder.Services.AddHostedTemporalWorker(taskQueue).AddDurableAI(options =>
        {
            options.ActivityTimeout = TimeSpan.FromSeconds(30);
            options.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            options.SessionTimeToLive = TimeSpan.FromMinutes(5);
            options.MaximumConsecutiveErrorsPerRequest = 2;
            options.RetryPolicy = new RetryPolicy
            {
                MaximumAttempts = 3,
                InitialInterval = TimeSpan.FromMilliseconds(10),
                MaximumInterval = TimeSpan.FromMilliseconds(10),
            };
            configure?.Invoke(options);
        });
        return builder.Build();
    }

    private sealed class FailingReducer(string scenario) : IChatReducer
    {
        public ConcurrentQueue<ActivityInfo> Calls { get; } = new();

        public Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(ActivityExecutionContext.Current.Info);
            if (scenario == "permanent-reducer")
            {
                throw new ApplicationFailureException(
                    Secret,
                    errorType: "CustomReducerPermanent",
                    nonRetryable: true,
                    details: [Secret]);
            }

            return Task.FromResult<IEnumerable<ChatMessage>>(
                [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphan", "invalid")])]);
        }
    }

    private sealed class RecordingChatClient(Func<int, Exception?>? failureFactory = null) : IChatClient
    {
        private int _calls;

        public ConcurrentQueue<ActivityInfo> Calls { get; } = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(ActivityExecutionContext.Current.Info);
            var failure = failureFactory?.Invoke(Interlocked.Increment(ref _calls));
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
