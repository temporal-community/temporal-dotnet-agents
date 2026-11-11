using System.Reflection;
using AmbientAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using Xunit;
using Legacy = TemporalCommunity.Extensions.Agents.IntegrationTests.LegacyAmbientAgent;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AmbientAgentHistoricalReplayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalHistory_WithQueuedWorkAtShutdown_ReplaysWithActualSample(bool alertWorkflow)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var client = environment.Client;
        var queue = $"ambient-historical-replay-{Guid.NewGuid():N}";
        var gate = new HoldFirstRunCompletion();
        var model = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "NORMAL: metrics stable."))]);
        using var host = CreateLegacyHost(client, queue, gate, model, alertWorkflow);
        await host.StartAsync();
        WorkflowHistory history;
        try
        {
            if (alertWorkflow)
            {
                var handle = await client.StartWorkflowAsync(
                    (Legacy.AlertWorkflow workflow) => workflow.RunAsync(),
                    new WorkflowOptions(queue, queue));
                await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(1)));
                await gate.Held.WaitAsync(Timeout);
                await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(2)));
                await handle.SignalAsync(workflow => workflow.ShutdownAsync());
                gate.Release();
                await handle.GetResultAsync().WaitAsync(Timeout);
                Assert.Single(await handle.QueryAsync(workflow => workflow.GetNotifications()));
                history = await handle.FetchHistoryAsync();
            }
            else
            {
                var handle = await client.StartWorkflowAsync(
                    (Legacy.MonitorWorkflow workflow) => workflow.RunAsync(new Legacy.MonitorWorkflowInput
                    {
                        AlertWorkflowId = "unused-no-anomaly",
                        AnalysisInterval = 2,
                        MaxBufferSize = 4,
                    }),
                    new WorkflowOptions(queue, queue));
                // This case starts after legacy RunAsync has initialized its counters.
                // Early signals are covered separately below.
                var initial = await handle.QueryAsync(workflow => workflow.GetStatus());
                Assert.Equal(0, initial.TotalReadings);
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(1)));
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(2)));
                await gate.Held.WaitAsync(Timeout);
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(3)));
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(4)));
                await handle.SignalAsync(workflow => workflow.ShutdownAsync());
                gate.Release();
                await handle.GetResultAsync().WaitAsync(Timeout);
                var status = await handle.QueryAsync(workflow => workflow.GetStatus());
                Assert.Equal(4, status.TotalReadings);
                Assert.Single(status.RecentAnalyses);
                history = await handle.FetchHistoryAsync();
            }
        }
        finally
        {
            gate.Release();
            await host.StopAsync();
        }

        Assert.Equal(1, model.CallCount);
        var scheduled = Assert.Single(history.Events,
            item => item.ActivityTaskScheduledEventAttributes is not null);
        Assert.Equal("Run", scheduled.ActivityTaskScheduledEventAttributes.ActivityType.Name);
        var completed = Assert.Single(history.Events,
            item => item.ActivityTaskCompletedEventAttributes is not null);
        Assert.Equal(scheduled.EventId, completed.ActivityTaskCompletedEventAttributes.ScheduledEventId);
        var signals = history.Events.Where(
            item => item.WorkflowExecutionSignaledEventAttributes is not null).ToArray();
        Assert.Equal(alertWorkflow ? 3 : 5, signals.Length);
        Assert.Equal("Shutdown", signals[^1].WorkflowExecutionSignaledEventAttributes.SignalName);
        // A complete second batch/alert and shutdown must precede the held activity's completion.
        Assert.All(signals.TakeLast(alertWorkflow ? 2 : 3),
            item => Assert.InRange(item.EventId, scheduled.EventId + 1, completed.EventId - 1));
        Assert.NotNull(history.Events.Last().WorkflowExecutionCompletedEventAttributes);
        Assert.DoesNotContain(history.Events, item => item.MarkerRecordedEventAttributes is not null);
        Assert.DoesNotContain(history.Events, item => item.WorkflowTaskFailedEventAttributes is not null);

        await VerifyReplayAsync(history, client, alertWorkflow);
    }

    [Fact]
    public async Task OriginalMonitorHistory_WithSignalsBeforeFirstTask_PreservesLegacyInitializationOnReplay()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var client = environment.Client;
        var queue = $"ambient-historical-initial-signals-{Guid.NewGuid():N}";
        var gate = new HoldFirstRunCompletion();
        var model = new ScriptedChatClient(
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, "NORMAL: metrics stable."))]);
        using var host = CreateLegacyHost(client, queue, gate, model, alertWorkflow: false);
        var handle = await client.StartWorkflowAsync(
            (Legacy.MonitorWorkflow workflow) => workflow.RunAsync(new Legacy.MonitorWorkflowInput
            {
                AlertWorkflowId = "unused-no-anomaly",
                AnalysisInterval = 2,
                MaxBufferSize = 3,
                CarriedBuffer = new List<HealthCheckData> { Reading(1), Reading(2) },
                CarriedTotalReadings = 2,
                CarriedReadingsSinceLastAnalysis = 1,
            }),
            new WorkflowOptions(queue, queue));
        // No worker is polling yet, so both signals necessarily precede the first workflow task.
        await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(3)));
        await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(4)));
        await host.StartAsync();
        WorkflowHistory history;
        try
        {
            var initial = await handle.QueryAsync(workflow => workflow.GetStatus());
            // Legacy RunAsync appends carried readings and overwrites counters after early signals.
            Assert.Equal(2, initial.TotalReadings);
            Assert.Equal(4, initial.BufferSize);
            Assert.Empty(initial.RecentAnalyses);
            await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(5)));
            await gate.Held.WaitAsync(Timeout);
            await handle.SignalAsync(workflow => workflow.ShutdownAsync());
            gate.Release();
            await handle.GetResultAsync().WaitAsync(Timeout);
            var final = await handle.QueryAsync(workflow => workflow.GetStatus());
            Assert.Equal(3, final.TotalReadings);
            Assert.Equal(3, final.BufferSize);
            Assert.Single(final.RecentAnalyses);
            history = await handle.FetchHistoryAsync();
        }
        finally
        {
            gate.Release();
            await host.StopAsync();
        }

        var call = Assert.Single(model.Calls);
        var prompt = string.Join("\n", call.Messages.Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text));
        Assert.Contains("[00:00:02]", prompt);
        Assert.Contains("[00:00:05]", prompt);
        Assert.DoesNotContain("[00:00:03]", prompt);
        Assert.DoesNotContain("[00:00:04]", prompt);
        var firstTask = history.Events.First(item => item.WorkflowTaskStartedEventAttributes is not null);
        var signals = history.Events.Where(item => item.WorkflowExecutionSignaledEventAttributes is not null).ToArray();
        Assert.Equal(4, signals.Length);
        Assert.All(signals.Take(2), item => Assert.True(item.EventId < firstTask.EventId));
        Assert.Single(history.Events, item => item.ActivityTaskScheduledEventAttributes is not null);
        Assert.NotNull(history.Events.Last().WorkflowExecutionCompletedEventAttributes);
        Assert.DoesNotContain(history.Events, item => item.MarkerRecordedEventAttributes is not null);
        Assert.DoesNotContain(history.Events, item => item.WorkflowTaskFailedEventAttributes is not null);
        await VerifyReplayAsync(history, client, alertWorkflow: false);
    }

    private static IHost CreateLegacyHost(
        ITemporalClient client, string queue, HoldFirstRunCompletion gate, IChatClient model, bool alertWorkflow)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [gate])
            // These have the original workflow names. Never register the current definitions here.
            .AddWorkflow<Legacy.MonitorWorkflow>()
            .AddWorkflow<Legacy.AlertWorkflow>()
            .AddScopedActivities<AgentInvocationActivities>()
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent(alertWorkflow ? "AlertAgent" : "AnalysisAgent",
                    agent => agent.ChatClient = _ => model);
            });
        return builder.Build();
    }

    private static async Task VerifyReplayAsync(WorkflowHistory history, ITemporalClient client, bool alertWorkflow)
    {
        var replay = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = client.Options.DataConverter,
        }.AddWorkflow<MonitorWorkflow>().AddWorkflow<AlertWorkflow>());
        var result = await replay.ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        Assert.Null(result.ReplayFailure);

        // Force modern run/signal logic on an unpatched history. Monitor construction still used
        // legacy initialization before the field is changed.
        var unversionedDefinition = alertWorkflow
            ? ForceNewPath<UnversionedAlertWorkflow, AlertWorkflow>(
                "AmbientAgent.AlertWorkflow", "_drainOnShutdown",
                _ => new AlertWorkflow(), actual => new(actual))
            : ForceNewPath<UnversionedMonitorWorkflow, MonitorWorkflow>(
                "AmbientAgent.MonitorWorkflow", "_useFifoDrain",
                args => new MonitorWorkflow((MonitorWorkflowInput)args[0]!), actual => new(actual));
        var negativeReplay = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = client.Options.DataConverter,
        }.AddWorkflow(unversionedDefinition));
        var negativeResult = await negativeReplay.ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        Assert.NotNull(negativeResult.ReplayFailure);
        Assert.Contains("nondetermin", negativeResult.ReplayFailure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowDefinition ForceNewPath<TWorkflow, TActual>(
        string workflowName, string branchField,
        Func<object?[], TActual> create, Func<TActual, TWorkflow> wrap)
        where TWorkflow : class
        where TActual : class
    {
        var field = typeof(TActual).GetField(branchField, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return WorkflowDefinition.Create(typeof(TWorkflow), workflowName, args =>
        {
            var workflow = create(args);
            field.SetValue(workflow, true);
            return wrap(workflow);
        });
    }

    // SDK disallows overriding WorkflowInit factories and inheriting WorkflowRun methods.
    // These negative-control adapters delegate every command and signal to the actual sample.
    [Workflow("AmbientAgent.MonitorWorkflow")]
    public sealed class UnversionedMonitorWorkflow(MonitorWorkflow actual)
    {
        [WorkflowRun]
        public Task RunAsync(MonitorWorkflowInput input) => actual.RunAsync(input);

        [WorkflowSignal("IngestHealthCheck")]
        public Task IngestHealthCheckAsync(HealthCheckData reading) => actual.IngestHealthCheckAsync(reading);

        [WorkflowSignal("Shutdown")]
        public Task ShutdownAsync() => actual.ShutdownAsync();
    }

    [Workflow("AmbientAgent.AlertWorkflow")]
    public sealed class UnversionedAlertWorkflow(AlertWorkflow actual)
    {
        [WorkflowRun]
        public Task RunAsync() => actual.RunAsync();

        [WorkflowSignal("IngestAnomaly")]
        public Task IngestAnomalyAsync(AnomalyAlert alert) => actual.IngestAnomalyAsync(alert);

        [WorkflowSignal("Shutdown")]
        public Task ShutdownAsync() => actual.ShutdownAsync();
    }

    private static HealthCheckData Reading(int index) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(index), index, index, index);

    private static AnomalyAlert Alert(int index) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(index), $"anomaly {index}", [Reading(index)]);

    private sealed class HoldFirstRunCompletion : IWorkerInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public Task Held => _held.Task;
        public void Release() => _release.TrySetResult();

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new Inbound(nextInterceptor, this);

        private sealed class Inbound(ActivityInboundInterceptor next, HoldFirstRunCompletion owner)
            : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                var result = await base.ExecuteActivityAsync(input);
                if (ActivityExecutionContext.Current.Info.ActivityType == "Run"
                    && Interlocked.Increment(ref owner._runCount) == 1)
                {
                    owner._held.TrySetResult();
                    await owner._release.Task.WaitAsync(ActivityExecutionContext.Current.CancellationToken);
                }
                return result;
            }
        }
    }
}
