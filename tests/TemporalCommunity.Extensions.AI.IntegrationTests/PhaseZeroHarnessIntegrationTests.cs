using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class PhaseZeroHarnessIntegrationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TimerOnly_TimeSkippingAdvancesWorkflowClockAndReplays()
    {
        await using var environment = await DurableChatTestEnvironment.StartTimeSkippingAsync();
        var systemInfo = await environment.Client.WorkflowService.GetSystemInfoAsync(new GetSystemInfoRequest());
        output.WriteLine($"Time-skipping test server reports: '{systemInfo.ServerVersion}' (not the local service baseline)");
        var queue = $"timer-harness-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<TimerProbe>());
        await worker.ExecuteAsync(async () =>
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (TimerProbe workflow) => workflow.RunAsync(TimeSpan.FromDays(3)),
                new($"timer-{Guid.NewGuid():N}", queue));
            var elapsed = await handle.GetResultAsync().WaitAsync(Deadline);
            Assert.InRange(elapsed, TimeSpan.FromDays(3), TimeSpan.FromDays(3) + Deadline);
            var history = await handle.FetchHistoryAsync();
            var timer = Assert.Single(history.Events, item => item.EventType == EventType.TimerStarted);
            Assert.Equal(TimeSpan.FromDays(3), timer.TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan());
            Assert.Single(history.Events, item => item.EventType == EventType.TimerFired);
            var replay = await new WorkflowReplayer(new WorkflowReplayerOptions
                {
                    DataConverter = DurableAIDataConverter.Instance,
                }.AddWorkflow<TimerProbe>()).ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
        });
    }

    [Fact]
    public async Task ValidatorRejections_DoNotPersistEvents_AndHistoryCountersGrowOnAcceptedWork()
    {
        await using var environment = await DurableChatTestEnvironment.StartLocalAsync();
        var systemInfo = await environment.Client.WorkflowService.GetSystemInfoAsync(new GetSystemInfoRequest());
        output.WriteLine($"Local service reports: {systemInfo.ServerVersion}");
        Assert.Equal(new Version(1, 31, 2),
            TemporalServiceTestEnvironment.ParseAndValidateServerVersion(systemInfo.ServerVersion));
        var queue = $"history-harness-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<HistoryProbe>());
        await worker.ExecuteAsync(async () =>
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (HistoryProbe workflow) => workflow.RunAsync(),
                new($"history-{Guid.NewGuid():N}", queue));
            var first = await handle.ExecuteUpdateAsync(workflow => workflow.AcceptAsync("first", false)).WaitAsync(Deadline);
            var before = await handle.FetchHistoryAsync();
            for (var index = 0; index < 10; index++)
            {
                var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                    handle.ExecuteUpdateAsync(workflow => workflow.AcceptAsync("reject", false)).WaitAsync(Deadline));
                Assert.Contains("rejected by validator", failure.ToString());
            }
            var after = await handle.FetchHistoryAsync();
            Assert.Equal(before.Events.Select(item => (item.EventId, item.EventType)),
                after.Events.Select(item => (item.EventId, item.EventType)));
            var second = await handle.ExecuteUpdateAsync(workflow => workflow.AcceptAsync("second", false)).WaitAsync(Deadline);
            Assert.True(first.Length > 0);
            Assert.True(first.Bytes > 0);
            Assert.True(second.Length > first.Length);
            Assert.True(second.Bytes > first.Bytes);
            Assert.Equal(1, first.Accepted);
            Assert.Equal(2, second.Accepted);
            await handle.ExecuteUpdateAsync(workflow => workflow.AcceptAsync("finish", true)).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
            var history = await handle.FetchHistoryAsync();
            Assert.Equal(3, history.Events.Count(item => item.EventType == EventType.WorkflowExecutionUpdateAccepted));
            Assert.Equal(3, history.Events.Count(item => item.EventType == EventType.WorkflowExecutionUpdateCompleted));
            var replay = await new WorkflowReplayer(new WorkflowReplayerOptions
                {
                    DataConverter = DurableAIDataConverter.Instance,
                }.AddWorkflow<HistoryProbe>()).ReplayWorkflowAsync(history, throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
        });
    }

    [Fact]
    public async Task ControlledRetry_ExposesEffectsBeforeCompletion_AndCompletedHistoryDoesNotRepeatThem()
    {
        await using var environment = await DurableChatTestEnvironment.StartLocalAsync();
        var first = new ControlledActivityAttempt<ChatMessage>();
        var second = new ControlledActivityAttempt<ChatMessage>();
        var activities = new ControlledToolActivities(first, second);
        var queue = $"effect-harness-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<EffectProbe>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("logical-call", "write")]);
            var handle = await environment.Client.StartWorkflowAsync(
                (EffectProbe workflow) => workflow.RunAsync(call, false),
                new($"effect-{Guid.NewGuid():N}", queue));
            var resultTask = handle.GetResultAsync();
            try
            {
                await first.Entered.WaitAsync(Deadline);
                Assert.Equal(1, activities.Effects);
                Assert.False(resultTask.IsCompleted);
                first.Fail(new InvalidOperationException("effect applied but response lost"));
                await second.Entered.WaitAsync(Deadline);
                Assert.Equal(2, activities.Effects);
                second.Complete(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("logical-call", "written")]));
                var result = await resultTask.WaitAsync(Deadline);
                Assert.Equal("logical-call", Assert.IsType<FunctionResultContent>(result.Contents.Single()).CallId);
                Assert.Equal("logical-call", Assert.IsType<FunctionCallContent>(
                    Assert.Single(activities.Inputs).Contents.Single()).CallId);
                var replay = await new WorkflowReplayer(new WorkflowReplayerOptions
                    {
                        DataConverter = DurableAIDataConverter.Instance,
                    }.AddWorkflow<EffectProbe>()).ReplayWorkflowAsync(
                        await handle.FetchHistoryAsync(), throwOnReplayFailure: false);
                Assert.Null(replay.ReplayFailure);
                Assert.Equal(2, activities.Effects);
            }
            finally
            {
                // Release a still-blocked attempt if an assertion failed.
                if (!first.IsCompletionSupplied)
                    first.Complete(new ChatMessage(ChatRole.Tool, "cleanup"));
                if (!second.IsCompletionSupplied)
                    second.Complete(new ChatMessage(ChatRole.Tool, "cleanup"));
            }
        });
    }

    [Fact]
    public async Task WorkerRestart_RestoresCompletedActivityWithoutRepeatingEffect()
    {
        await using var environment = await DurableChatTestEnvironment.StartLocalAsync();
        var first = new ControlledActivityAttempt<ChatMessage>();
        var unusedRetry = new ControlledActivityAttempt<ChatMessage>();
        first.Complete(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("restart-call", "written")]));
        var activities = new ControlledToolActivities(first, unusedRetry);
        var queue = $"restart-harness-{Guid.NewGuid():N}";
        var id = $"restart-{Guid.NewGuid():N}";
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("restart-call", "write")]);
        using (var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<EffectProbe>().AddAllActivities(activities)))
        {
            await worker.ExecuteAsync(async () =>
            {
                var started = await environment.Client.StartWorkflowAsync(
                    (EffectProbe workflow) => workflow.RunAsync(call, true), new(id, queue));
                var completed = await started.ExecuteUpdateAsync(workflow => workflow.ReadCompletedAsync()).WaitAsync(Deadline);
                Assert.Equal("restart-call", Assert.IsType<FunctionResultContent>(completed.Contents.Single()).CallId);
                Assert.Equal(1, activities.Effects);
            });
        }

        using var replacement = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<EffectProbe>().AddAllActivities(activities));
        await replacement.ExecuteAsync(async () =>
        {
            var handle = environment.Client.GetWorkflowHandle<EffectProbe>(id);
            await handle.ExecuteUpdateAsync(workflow => workflow.FinishAsync()).WaitAsync(Deadline);
            await handle.GetResultAsync().WaitAsync(Deadline);
            Assert.Equal(1, activities.Effects);
            Assert.Equal(0, unusedRetry.Invocations);
        });
    }

    [Workflow]
    public class TimerProbe
    {
        [WorkflowRun]
        public async Task<TimeSpan> RunAsync(TimeSpan delay)
        {
            var start = Workflow.UtcNow;
            await Workflow.DelayAsync(delay);
            return Workflow.UtcNow - start;
        }
    }

    public sealed record HistorySnapshot(int Length, int Bytes, int Accepted);

    [Workflow]
    public class HistoryProbe
    {
        private bool _finished;
        private int _accepted;

        [WorkflowRun]
        public Task RunAsync() => Workflow.WaitConditionAsync(() => _finished && Workflow.AllHandlersFinished);

        [WorkflowUpdateValidator(nameof(AcceptAsync))]
        public void Validate(string value, bool finish)
        {
            if (value == "reject")
                throw new ApplicationFailureException("rejected by validator", nonRetryable: true);
        }

        [WorkflowUpdate]
        public Task<HistorySnapshot> AcceptAsync(string value, bool finish)
        {
            _finished = finish;
            return Task.FromResult(new HistorySnapshot(
                Workflow.CurrentHistoryLength, Workflow.CurrentHistorySize, ++_accepted));
        }
    }

    [Workflow]
    public class EffectProbe
    {
        private ChatMessage? _result;
        private bool _finished;

        [WorkflowRun]
        public async Task<ChatMessage> RunAsync(ChatMessage call, bool waitForFinish)
        {
            _result = await Workflow.ExecuteActivityAsync((ControlledToolActivities activities) => activities.WriteAsync(call),
                new()
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(15),
                    RetryPolicy = new RetryPolicy
                    {
                        MaximumAttempts = 2,
                        InitialInterval = TimeSpan.FromMilliseconds(10),
                    },
                });
            if (waitForFinish)
                await Workflow.WaitConditionAsync(() => _finished && Workflow.AllHandlersFinished);
            return _result;
        }

        [WorkflowUpdate]
        public async Task<ChatMessage> ReadCompletedAsync()
        {
            await Workflow.WaitConditionAsync(() => _result is not null);
            return _result!;
        }

        [WorkflowUpdate]
        public Task FinishAsync()
        {
            _finished = true;
            return Task.CompletedTask;
        }
    }

    public class ControlledToolActivities(
        ControlledActivityAttempt<ChatMessage> first, ControlledActivityAttempt<ChatMessage> second)
    {
        private int _effects;
        public int Effects => Volatile.Read(ref _effects);
        public List<ChatMessage> Inputs { get; } = [];

        [Activity]
        public Task<ChatMessage> WriteAsync(ChatMessage call)
        {
            var attempt = ActivityExecutionContext.Current.Info.Attempt;
            if (attempt == 1)
                Inputs.Add(call);
            return (attempt == 1 ? first : second).ExecuteAsync(
                () => Interlocked.Increment(ref _effects),
                ActivityExecutionContext.Current.CancellationToken);
        }
    }
}
