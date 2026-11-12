using AmbientAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Xunit;
using AmbientAgentInvocationActivities = AmbientAgent.AgentInvocationActivities;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AmbientAgentBatchDrainIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDrainFailsAfterThreeRetryableInvocationFailures(bool alertWorkflow)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var chatClient = new RecordingChatClient();
        var failures = new FailAgentInvocations();
        var queue = $"ambient-retry-bound-{Guid.NewGuid():N}";
        var workflowId = $"ambient-retry-bound-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [failures])
            .AddWorkflow<MonitorWorkflow>()
            .AddWorkflow<AlertWorkflow>()
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            Task result;
            if (alertWorkflow)
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (AlertWorkflow workflow) => workflow.RunAsync(), new WorkflowOptions(workflowId, queue));
                await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(1)));
                await failures.FirstAttemptStarted.WaitAsync(TimeSpan.FromSeconds(15));
                await handle.SignalAsync(workflow => workflow.ShutdownAsync());
                result = handle.GetResultAsync();
            }
            else
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (MonitorWorkflow workflow) => workflow.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = "unused",
                        AnalysisInterval = 1
                    }), new WorkflowOptions(workflowId, queue));
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(1)));
                await failures.FirstAttemptStarted.WaitAsync(TimeSpan.FromSeconds(15));
                await handle.SignalAsync(workflow => workflow.ShutdownAsync());
                result = handle.GetResultAsync();
            }

            failures.Release();
            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                () => result.WaitAsync(TimeSpan.FromSeconds(15)));
            var activity = Assert.IsType<ActivityFailureException>(failure.InnerException);
            Assert.Equal(Temporalio.Api.Enums.V1.RetryState.MaximumAttemptsReached, activity.RetryState);
            var application = Assert.IsType<ApplicationFailureException>(activity.InnerException);
            Assert.Equal(nameof(InvalidOperationException), application.ErrorType);
            Assert.Contains("Persistent retryable invocation failure", application.Message);
            Assert.Equal(3, failures.Attempts);
            Assert.Empty(chatClient.Calls);
            await CaptureHistoryAsync(environment.Client, workflowId);
        }
        finally
        {
            failures.Release();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MonitorRestoresCarriedReadingsBeforeSignalsInTheFirstWorkflowTask()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var chatClient = new RecordingChatClient();
        var queue = $"ambient-initial-signals-{Guid.NewGuid():N}";
        var workflowId = $"ambient-initial-signals-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .AddWorkflow<MonitorWorkflow>()
            .AddTemporalAgents(options =>
                options.AddDurableAgent("AnalysisAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>()))
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        var monitor = await environment.Client.StartWorkflowAsync(
            (MonitorWorkflow workflow) => workflow.RunAsync(new MonitorWorkflowInput
            {
                AlertWorkflowId = "unused",
                AnalysisInterval = 5,
                MaxBufferSize = 5,
                CarriedBuffer = new List<HealthCheckData> { Reading(2) },
                CarriedPendingAnalysisReadings = new List<HealthCheckData> { Reading(1), Reading(2) },
                CarriedTotalReadings = 2
            }),
            new WorkflowOptions(workflowId, queue));

        for (var index = 3; index <= 7; index++)
            await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(index)));
        await monitor.SignalAsync(workflow => workflow.ShutdownAsync());
        await host.StartAsync();
        try
        {
            await monitor.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var status = await monitor.QueryAsync(workflow => workflow.GetStatus());
            Assert.Equal(7, status.TotalReadings);
            Assert.Equal(5, status.BufferSize);
            Assert.Equal(2, status.PendingAnalysisReadings);
            Assert.Single(chatClient.Calls);
            AssertBatch(chatClient.Calls[0], 1, 5);
            await CaptureHistoryAsync(environment.Client, workflowId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalAgentFailureFailsWorkflowInsteadOfReportingSuccessfulDrain(bool alertWorkflow)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var chatClient = new RecordingChatClient(fail: true);
        var queue = $"ambient-terminal-failure-{Guid.NewGuid():N}";
        var workflowId = $"ambient-terminal-failure-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .AddWorkflow<MonitorWorkflow>()
            .AddWorkflow<AlertWorkflow>()
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("AnalysisAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
                options.AddDurableAgent("AlertAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
            })
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            if (alertWorkflow)
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (AlertWorkflow workflow) => workflow.RunAsync(),
                    new WorkflowOptions(workflowId, queue));
                await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(1)));
                await AssertTerminalFailureAsync(handle.GetResultAsync());
            }
            else
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (MonitorWorkflow workflow) => workflow.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = "unused",
                        AnalysisInterval = 1
                    }),
                    new WorkflowOptions(workflowId, queue));
                await handle.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(1)));
                await AssertTerminalFailureAsync(handle.GetResultAsync());
            }

            Assert.Single(chatClient.Calls);
            await CaptureHistoryAsync(environment.Client, workflowId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task AssertTerminalFailureAsync(Task result)
    {
        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => result.WaitAsync(TimeSpan.FromSeconds(15)));
        var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
        var applicationFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);
        Assert.Equal("DurableTurnFailedException", applicationFailure.ErrorType);
        Assert.Equal(Temporalio.Api.Enums.V1.RetryState.NonRetryableFailure, activityFailure.RetryState);
    }

    [Fact]
    public async Task AlertDrainsSignalsReceivedWhileItsAgentActivityIsRunning()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var chatClient = new RecordingChatClient();
        var activityGate = new HoldFirstAgentInvocationActivity();
        var queue = $"ambient-alert-drain-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [activityGate])
            .AddWorkflow<AlertWorkflow>()
            .AddTemporalAgents(options =>
                options.AddDurableAgent("AlertAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>()))
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        var workflowId = $"ambient-alert-{Guid.NewGuid():N}";
        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (AlertWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(workflowId, queue));

            await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(1)));
            await activityGate.FirstInvocationCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(2)));
            await handle.SignalAsync(workflow => workflow.ShutdownAsync());
            await handle.SignalAsync(workflow => workflow.IngestAnomalyAsync(Alert(3)));
            activityGate.ReleaseFirstInvocation();

            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, chatClient.Calls.Count);
            await CaptureHistoryAsync(environment.Client, workflowId);
        }
        finally
        {
            activityGate.ReleaseFirstInvocation();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MonitorDrainsAnomalyToAlertWithTheOriginalAnalysisBatch()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        var chatClient = new RecordingChatClient(anomalyFirst: true);
        var activityGate = new HoldFirstAgentInvocationActivity();
        var queue = $"ambient-anomaly-batch-{Guid.NewGuid():N}";
        var alertId = $"ambient-alert-{Guid.NewGuid():N}";
        var monitorId = $"ambient-monitor-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [activityGate])
            .AddWorkflow<MonitorWorkflow>()
            .AddWorkflow<AlertWorkflow>()
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("AnalysisAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
                options.AddDurableAgent("AlertAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
            })
            .AddSingletonActivities<AlertActivities>()
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var alerts = await environment.Client.StartWorkflowAsync(
                (AlertWorkflow workflow) => workflow.RunAsync(), new WorkflowOptions(alertId, queue));
            var monitor = await environment.Client.StartWorkflowAsync(
                (MonitorWorkflow workflow) => workflow.RunAsync(new MonitorWorkflowInput
                {
                    AlertWorkflowId = alertId,
                    AnalysisInterval = 5,
                    MaxBufferSize = 5
                }), new WorkflowOptions(monitorId, queue));

            for (var index = 1; index <= 5; index++)
                await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(index)));
            await activityGate.FirstInvocationCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            for (var index = 6; index <= 10; index++)
                await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(index)));
            await monitor.SignalAsync(workflow => workflow.ShutdownAsync());
            activityGate.ReleaseFirstInvocation();

            await monitor.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await alerts.SignalAsync(workflow => workflow.ShutdownAsync());
            await alerts.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));

            var notifications = await alerts.QueryAsync(workflow => workflow.GetNotifications());
            Assert.Single(notifications);
            var alertPrompt = Assert.Single(chatClient.Calls,
                call => call.Contains("Compose a concise alert notification", StringComparison.Ordinal));
            AssertBatch(alertPrompt, 1, 5);
            Assert.DoesNotContain("[00:00:06]", alertPrompt);
            await CaptureHistoryAsync(environment.Client, monitorId);
            await CaptureHistoryAsync(environment.Client, alertId);
        }
        finally
        {
            activityGate.ReleaseFirstInvocation();
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(20, false)]
    [InlineData(12, true)]
    public async Task MonitorDrainsFifoBatchesOnShutdownAndLeavesOnlyTrailingPartialBatch(
        int readingCount, bool continueAsNew)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync(
            continueAsNew
                ? ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=20"]
                : []);
        var chatClient = new RecordingChatClient();
        var activityGate = new HoldFirstAgentInvocationActivity();
        var queue = $"ambient-agent-batch-drain-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [activityGate])
            .AddWorkflow<MonitorWorkflow>()
            .AddWorkflow<AlertWorkflow>()
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("AnalysisAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
                options.AddDurableAgent("AlertAgent", agent =>
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>());
            })
            .AddSingletonActivities<AlertActivities>()
            .AddScopedActivities<AmbientAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        var alertId = $"ambient-alert-{Guid.NewGuid():N}";
        var monitorId = $"ambient-monitor-{Guid.NewGuid():N}";
        string? initialRunId = null;
        try
        {
            await environment.Client.StartWorkflowAsync(
                (AlertWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(alertId, queue));

            var monitor = await environment.Client.StartWorkflowAsync(
                (MonitorWorkflow workflow) => workflow.RunAsync(new MonitorWorkflowInput
                {
                    AlertWorkflowId = alertId,
                    AnalysisInterval = 5,
                    MaxBufferSize = 5
                }),
                new WorkflowOptions(monitorId, queue));
            initialRunId = (await monitor.DescribeAsync()).RunId;

            for (var index = 1; index <= 5; index++)
                await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(index)));

            await activityGate.FirstInvocationCompleted.WaitAsync(TimeSpan.FromSeconds(30));

            for (var index = 6; index <= readingCount; index++)
                await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(index)));

            var queuedStatus = await monitor.QueryAsync(workflow => workflow.GetStatus());
            var ingestionDeadline = DateTime.UtcNow.AddSeconds(15);
            while (queuedStatus.TotalReadings < readingCount && DateTime.UtcNow < ingestionDeadline)
            {
                await Task.Delay(25);
                queuedStatus = await monitor.QueryAsync(workflow => workflow.GetStatus());
            }
            Assert.Equal(readingCount, queuedStatus.TotalReadings);
            Assert.Equal(5, queuedStatus.BufferSize);
            Assert.Equal(readingCount - 5, queuedStatus.PendingAnalysisReadings);

            if (continueAsNew)
            {
                activityGate.ReleaseFirstInvocation();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while ((await monitor.DescribeAsync()).RunId == initialRunId && DateTime.UtcNow < deadline)
                    await Task.Delay(25);
                Assert.NotEqual(initialRunId, (await monitor.DescribeAsync()).RunId);
            }

            await monitor.SignalAsync(workflow => workflow.ShutdownAsync());
            if (!continueAsNew)
                await monitor.SignalAsync(workflow => workflow.IngestHealthCheckAsync(Reading(99)));
            activityGate.ReleaseFirstInvocation();
            await monitor.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var finalStatus = await monitor.QueryAsync(workflow => workflow.GetStatus());

            var alertHandle = environment.Client.GetWorkflowHandle<AlertWorkflow>(alertId);
            await alertHandle.SignalAsync(workflow => workflow.ShutdownAsync());
            await alertHandle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var notifications = await alertHandle.QueryAsync(workflow => workflow.GetNotifications());

            Assert.Equal(readingCount, finalStatus.TotalReadings);
            Assert.Equal(5, finalStatus.BufferSize);
            Assert.Equal(readingCount / 5 - (continueAsNew ? 1 : 0), finalStatus.RecentAnalyses.Count);
            Assert.Equal(readingCount % 5, finalStatus.PendingAnalysisReadings);
            Assert.Empty(notifications);
            Assert.Equal(readingCount / 5, chatClient.Calls.Count);
            for (var batch = 0; batch < readingCount / 5; batch++)
                AssertBatch(chatClient.Calls[batch], batch * 5 + 1, batch * 5 + 5);
            await CaptureHistoryAsync(environment.Client, monitorId);
            if (initialRunId is not null)
                await CaptureHistoryAsync(environment.Client, monitorId, initialRunId);
            await CaptureHistoryAsync(environment.Client, alertId);
        }
        finally
        {
            activityGate.ReleaseFirstInvocation();
            await host.StopAsync();
        }
    }

    private static async Task CaptureHistoryAsync(ITemporalClient client, string workflowId, string? runId = null)
    {
        var directory = Path.Combine("artifacts", "ambient-batch-regression");
        Directory.CreateDirectory(directory);
        var history = await client.GetWorkflowHandle(workflowId, runId).FetchHistoryAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, $"{workflowId}-{runId ?? "latest"}.json"), history.ToJson());
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = client.Options.DataConverter
        }.AddWorkflow<MonitorWorkflow>().AddWorkflow<AlertWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    private static HealthCheckData Reading(int index) =>
        new(
            Timestamp: DateTimeOffset.UnixEpoch.AddSeconds(index),
            CpuPercent: index,
            MemoryPercent: index,
            TemperatureCelsius: index);

    private static AnomalyAlert Alert(int index) =>
        new(
            DetectedAt: DateTimeOffset.UnixEpoch.AddSeconds(index),
            Summary: $"anomaly {index}",
            RecentReadings: [Reading(index)]);

    private static void AssertBatch(string prompt, int first, int last)
    {
        var readings = prompt.Split('\n')
            .Where(line => line.Contains("] CPU=", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(last - first + 1, readings.Length);

        for (var index = first; index <= last; index++)
            Assert.Contains($"[00:00:{index:00}] CPU={index:F1}%", prompt);
    }

    private sealed class RecordingChatClient(bool fail = false, bool anomalyFirst = false) : IChatClient
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];

        public ChatClientMetadata Metadata { get; } = new("ambient-agent-test");

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                    return _calls.ToArray();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = string.Join(
                "\n",
                messages.SelectMany(message => message.Contents)
                    .OfType<TextContent>()
                    .Select(content => content.Text));
            bool anomaly;
            lock (_gate)
            {
                _calls.Add(prompt);
                anomaly = anomalyFirst && _calls.Count == 1;
            }

            if (fail)
                throw new ApplicationFailureException("Scripted terminal model failure", nonRetryable: true);

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant, anomaly ? "ANOMALY: first batch." : "NORMAL: metrics stable.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailAgentInvocations : IWorkerInterceptor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public Task FirstAttemptStarted => _started.Task;
        public int Attempts => Volatile.Read(ref _attempts);
        public void Release() => _release.TrySetResult();

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new Inbound(nextInterceptor, this);

        private sealed class Inbound(ActivityInboundInterceptor next, FailAgentInvocations owner)
            : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                if (ActivityExecutionContext.Current.Info.ActivityType != "Run")
                    return await base.ExecuteActivityAsync(input);
                Interlocked.Increment(ref owner._attempts);
                owner._started.TrySetResult();
                await owner._release.Task;
                throw new InvalidOperationException("Persistent retryable invocation failure.");
            }
        }
    }

    private sealed class HoldFirstAgentInvocationActivity : IWorkerInterceptor
    {
        private readonly TaskCompletionSource _firstInvocationCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task FirstInvocationCompleted => _firstInvocationCompleted.Task;

        public void ReleaseFirstInvocation() => _release.TrySetResult();

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new HoldActivityInbound(nextInterceptor, this);

        private sealed class HoldActivityInbound(
            ActivityInboundInterceptor next,
            HoldFirstAgentInvocationActivity owner) : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                var result = await base.ExecuteActivityAsync(input);
                var activityType = ActivityExecutionContext.Current.Info.ActivityType;
                if ((activityType == "Run" || activityType == "RunAsync"
                    || activityType.EndsWith(".RunAsync", StringComparison.Ordinal))
                    && Interlocked.CompareExchange(ref owner._held, 1, 0) == 0)
                {
                    owner._firstInvocationCompleted.TrySetResult();
                    await owner._release.Task;
                }

                return result;
            }
        }
    }
}
