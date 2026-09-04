#pragma warning disable MCPEXP001, MCPEXP002, MCPEXP004

using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Test-only research for mapping an MCP task onto Temporal-owned start, poll, and timer steps.
/// This is deliberately not a shipping library contract.
/// </summary>
public sealed class McpTasksTemporalLifecycleResearchTests
{
    [Fact]
    public async Task McpClientTool_DoesNotOptIntoRequiredTaskExecution()
    {
        var invocationCount = 0;
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        var mcpServices = new ServiceCollection();
        mcpServices
            .AddMcpServer()
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream())
            .WithTools(
            [
                McpServerTool.Create(
                    () =>
                    {
                        Interlocked.Increment(ref invocationCount);
                        return "report ready";
                    },
                    new() { Name = "run_report" }),
            ])
            .WithTasks(
                new InMemoryMcpTaskStore(),
                options => options.ExecutionModeSelector =
                    static _ => McpTaskExecutionMode.Required);

        await using var mcpServiceProvider = mcpServices.BuildServiceProvider();
        var mcpServer = mcpServiceProvider.GetRequiredService<McpServer>();
        _ = mcpServer.RunAsync();
        await using var mcpClient = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));

        var tool = Assert.Single(await mcpClient.ListToolsAsync());
        await Assert.ThrowsAsync<MissingRequiredClientCapabilityException>(async () =>
        {
            _ = await tool.InvokeAsync(new AIFunctionArguments());
        });
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task WorkflowOwnedPolling_PreservesTaskIdentityAcrossPollRetry_AndReplays()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"mcp-task-research-{Guid.NewGuid():N}";
        var gate = new McpTaskResearchGate();
        var tracker = new McpTaskResearchTracker { FailFirstPollAttempt = true };

        Pipe clientToServer = new();
        Pipe serverToClient = new();
        var taskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 25 };
        var mcpServices = new ServiceCollection();
        mcpServices
            .AddMcpServer()
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream())
            .WithTools(
            [
                McpServerTool.Create(
                    gate.RunReportAsync,
                    new() { Name = "run_report" }),
            ])
            .WithTasks(
                taskStore,
                options => options.ExecutionModeSelector =
                    static _ => McpTaskExecutionMode.Required);

        await using var mcpServiceProvider = mcpServices.BuildServiceProvider();
        var mcpServer = mcpServiceProvider.GetRequiredService<McpServer>();
        _ = mcpServer.RunAsync();

        await using var mcpClient = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));
        Assert.Equal("2026-07-28", mcpClient.NegotiatedProtocolVersion);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services.AddSingleton(mcpClient);
        builder.Services.AddSingleton(tracker);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<McpTaskLifecycleResearchWorkflow>()
            .AddScopedActivities<McpTaskLifecycleResearchActivities>();
        using var host = builder.Build();
        await host.StartAsync();

        var handle = await environment.Client.StartWorkflowAsync(
            (McpTaskLifecycleResearchWorkflow workflow) => workflow.RunAsync(),
            new WorkflowOptions($"mcp-task-research-{Guid.NewGuid():N}", taskQueue));

        await tracker.FirstPollAttempt.Task.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Complete("report ready");

        var result = await handle.GetResultAsync();
        Assert.Equal(McpTaskLifecycleState.Completed, result.State);
        Assert.Equal("report ready", result.ResultText);
        Assert.Equal(1, tracker.StartAttemptCount);
        Assert.True(tracker.PollAttemptCount >= 2);
        Assert.Single(tracker.ObservedTaskIds.Distinct(StringComparer.Ordinal));
        Assert.Equal(result.TaskId, tracker.ObservedTaskIds[0]);

        var scheduled = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.Equal(1, scheduled[McpTaskLifecycleResearchActivities.StartActivityName]);
        Assert.Equal(1, scheduled[McpTaskLifecycleResearchActivities.PollActivityName]);

        var timerCount = 0;
        await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
        {
            if (historyEvent.TimerStartedEventAttributes is not null)
            {
                timerCount++;
            }
        }
        Assert.True(timerCount >= 1);

        var history = await handle.FetchHistoryAsync();
        var replayerOptions = new WorkflowReplayerOptions();
        replayerOptions.AddWorkflow<McpTaskLifecycleResearchWorkflow>();
        var replayResult = await new WorkflowReplayer(replayerOptions)
            .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        Assert.Null(replayResult.ReplayFailure);

        await host.StopAsync();
    }

    [Fact]
    public async Task WorkflowOwnedCancellation_RequestsRemoteCancellation_AndObservesTerminalState()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"mcp-task-cancel-research-{Guid.NewGuid():N}";
        var gate = new McpTaskResearchGate();
        var tracker = new McpTaskResearchTracker();

        Pipe clientToServer = new();
        Pipe serverToClient = new();
        var mcpServices = new ServiceCollection();
        mcpServices
            .AddMcpServer()
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream())
            .WithTools(
            [
                McpServerTool.Create(
                    gate.RunReportAsync,
                    new() { Name = "run_report" }),
            ])
            .WithTasks(
                new InMemoryMcpTaskStore { DefaultPollIntervalMs = 25 },
                options => options.ExecutionModeSelector =
                    static _ => McpTaskExecutionMode.Required);

        await using var mcpServiceProvider = mcpServices.BuildServiceProvider();
        var mcpServer = mcpServiceProvider.GetRequiredService<McpServer>();
        _ = mcpServer.RunAsync();
        await using var mcpClient = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services.AddSingleton(mcpClient);
        builder.Services.AddSingleton(tracker);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<McpTaskLifecycleResearchWorkflow>()
            .AddScopedActivities<McpTaskLifecycleResearchActivities>();
        using var host = builder.Build();
        await host.StartAsync();

        var handle = await environment.Client.StartWorkflowAsync(
            (McpTaskLifecycleResearchWorkflow workflow) => workflow.RunAsync(),
            new WorkflowOptions($"mcp-task-cancel-research-{Guid.NewGuid():N}", taskQueue));
        var taskId = await WaitForTaskIdAsync(handle);
        await handle.SignalAsync(workflow => workflow.CancelAsync());

        var result = await handle.GetResultAsync();
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(McpTaskLifecycleState.Cancelled, result.State);
        Assert.Equal(1, tracker.CancelAttemptCount);
        await gate.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var scheduled = await WorkflowHistoryAssertions.CountAllScheduledByTypeAsync(handle);
        Assert.Equal(1, scheduled[McpTaskLifecycleResearchActivities.StartActivityName]);
        Assert.Equal(1, scheduled[McpTaskLifecycleResearchActivities.CancelActivityName]);
        Assert.True(scheduled[McpTaskLifecycleResearchActivities.PollActivityName] >= 1);

        await host.StopAsync();
    }

    private static async Task<string> WaitForTaskIdAsync(
        WorkflowHandle<McpTaskLifecycleResearchWorkflow, McpTaskLifecycleResearchResult> handle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var taskId = await handle.QueryAsync(workflow => workflow.GetTaskId());
            if (taskId is not null)
            {
                return taskId;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The workflow did not record an MCP task ID.");
    }
}

public enum McpTaskLifecycleState
{
    Working = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    InputRequired = 4,
}

public sealed record McpTaskStartResearchResult(
    bool IsTask,
    string? TaskId,
    long PollIntervalMs,
    McpTaskLifecycleState State,
    string? ResultText);

public sealed record McpTaskPollResearchInput(string TaskId);

public sealed record McpTaskPollResearchResult(
    string TaskId,
    long PollIntervalMs,
    McpTaskLifecycleState State,
    string? ResultText,
    string? Error);

public sealed record McpTaskLifecycleResearchResult(
    string? TaskId,
    McpTaskLifecycleState State,
    string? ResultText,
    string? Error);

[Workflow("TemporalCommunity.Extensions.AI.Tests.McpTaskLifecycleResearchWorkflow")]
public sealed class McpTaskLifecycleResearchWorkflow
{
    private bool _cancelRequested;
    private string? _taskId;

    [WorkflowRun]
    public async Task<McpTaskLifecycleResearchResult> RunAsync()
    {
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromMilliseconds(25),
                MaximumAttempts = 2,
            },
        };

        var started = await Workflow.ExecuteActivityAsync(
            (McpTaskLifecycleResearchActivities activities) => activities.StartAsync(),
            activityOptions);
        if (!started.IsTask)
        {
            return new(null, started.State, started.ResultText, null);
        }

        var taskId = started.TaskId!;
        _taskId = taskId;
        var pollIntervalMs = started.PollIntervalMs;
        var cancellationSent = false;
        while (true)
        {
            if (!cancellationSent)
            {
                var cancelRequested = await Workflow.WaitConditionAsync(
                    () => _cancelRequested,
                    TimeSpan.FromMilliseconds(Math.Max(1, pollIntervalMs)));
                if (cancelRequested)
                {
                    await Workflow.ExecuteActivityAsync(
                        (McpTaskLifecycleResearchActivities activities) =>
                            activities.CancelAsync(new McpTaskPollResearchInput(taskId)),
                        activityOptions);
                    cancellationSent = true;
                }
            }
            else
            {
                await Workflow.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(1, pollIntervalMs)));
            }

            var polled = await Workflow.ExecuteActivityAsync(
                (McpTaskLifecycleResearchActivities activities) =>
                    activities.PollAsync(new McpTaskPollResearchInput(taskId)),
                activityOptions);
            pollIntervalMs = polled.PollIntervalMs;

            if (polled.State == McpTaskLifecycleState.Working)
            {
                continue;
            }

            return new(polled.TaskId, polled.State, polled.ResultText, polled.Error);
        }
    }

    [WorkflowSignal]
    public Task CancelAsync()
    {
        _cancelRequested = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public string? GetTaskId() => _taskId;
}

public sealed class McpTaskLifecycleResearchActivities(
    McpClient client,
    McpTaskResearchTracker tracker)
{
    public const string StartActivityName =
        "TemporalCommunity.Extensions.AI.Tests.StartMcpTask";
    public const string PollActivityName =
        "TemporalCommunity.Extensions.AI.Tests.PollMcpTask";
    public const string CancelActivityName =
        "TemporalCommunity.Extensions.AI.Tests.CancelMcpTask";

    [Activity(StartActivityName)]
    public async Task<McpTaskStartResearchResult> StartAsync()
    {
        Interlocked.Increment(ref tracker.StartAttemptCount);
        var started = await client.CallToolAsTaskAsync(
            new CallToolRequestParams { Name = "run_report" },
            ActivityExecutionContext.Current.CancellationToken);

        if (!started.IsTask)
        {
            return new(
                false,
                null,
                0,
                McpTaskLifecycleState.Completed,
                ReadResultText(started.Result!));
        }

        var task = started.TaskCreated!;
        ActivityExecutionContext.Current.Heartbeat(
            new McpTaskPollResearchInput(task.TaskId));
        return new(
            true,
            task.TaskId,
            task.PollIntervalMs ?? 1000,
            McpTaskLifecycleState.Working,
            null);
    }

    [Activity(PollActivityName)]
    public async Task<McpTaskPollResearchResult> PollAsync(McpTaskPollResearchInput input)
    {
        tracker.ObservedTaskIds.Add(input.TaskId);
        var attempt = Interlocked.Increment(ref tracker.PollAttemptCount);
        if (tracker.FailFirstPollAttempt && attempt == 1)
        {
            tracker.FirstPollAttempt.TrySetResult();
            throw new InvalidOperationException("Injected poll-attempt failure.");
        }

        var result = await client.GetTaskAsync(
            input.TaskId,
            ActivityExecutionContext.Current.CancellationToken);
        var interval = result.PollIntervalMs ?? 1000;
        return result switch
        {
            WorkingTaskResult => new(
                input.TaskId,
                interval,
                McpTaskLifecycleState.Working,
                null,
                null),
            CompletedTaskResult completed => new(
                input.TaskId,
                interval,
                McpTaskLifecycleState.Completed,
                ReadResultText(completed.Result.Deserialize<CallToolResult>()!),
                null),
            FailedTaskResult failed => new(
                input.TaskId,
                interval,
                McpTaskLifecycleState.Failed,
                null,
                failed.Error.ToString()),
            CancelledTaskResult => new(
                input.TaskId,
                interval,
                McpTaskLifecycleState.Cancelled,
                null,
                null),
            InputRequiredTaskResult => new(
                input.TaskId,
                interval,
                McpTaskLifecycleState.InputRequired,
                null,
                "Input-required tasks are intentionally not implemented by this prototype."),
            _ => throw new InvalidOperationException(
                $"Unexpected MCP task result type '{result.GetType().Name}'."),
        };
    }

    [Activity(CancelActivityName)]
    public async Task CancelAsync(McpTaskPollResearchInput input)
    {
        Interlocked.Increment(ref tracker.CancelAttemptCount);
        await client.CancelTaskAsync(
            input.TaskId,
            ActivityExecutionContext.Current.CancellationToken);
    }

    private static string ReadResultText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
        ?? throw new InvalidOperationException("The research tool returned no text content.");
}

public sealed class McpTaskResearchTracker
{
    public int StartAttemptCount;
    public int PollAttemptCount;
    public int CancelAttemptCount;
    public bool FailFirstPollAttempt { get; init; }
    public TaskCompletionSource FirstPollAttempt { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> ObservedTaskIds { get; } = [];
}

public sealed class McpTaskResearchGate
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Description("Runs a report that completes after an external operation finishes.")]
    public TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> RunReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancellationObserved.TrySetResult();
            throw;
        }
    }

    public void Complete(string value) => _completion.TrySetResult(value);
}
