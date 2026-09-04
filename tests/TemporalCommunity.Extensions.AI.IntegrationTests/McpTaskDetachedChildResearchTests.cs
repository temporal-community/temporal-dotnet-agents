#pragma warning disable MCPEXP001, MCPEXP002, MCPEXP004

using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>Test-only detached-child comparison for the MCP Task research ADR.</summary>
public sealed class McpTaskDetachedChildResearchTests
{
    [Fact(Skip = "Non-shipping research prototype test; tracked in backlog")]
    public async Task DetachedExecutor_SignalsParentAfterParentContinueAsNew_AndBothReplay()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"mcp-task-child-research-{Guid.NewGuid():N}";
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
            .WithTools([
                McpServerTool.Create(gate.RunReportAsync, new() { Name = "run_report" }),
            ])
            .WithTasks(
                new InMemoryMcpTaskStore { DefaultPollIntervalMs = 25 },
                options => options.ExecutionModeSelector =
                    static _ => McpTaskExecutionMode.Required);

        await using var mcpProvider = mcpServices.BuildServiceProvider();
        var mcpServer = mcpProvider.GetRequiredService<McpServer>();
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
            .AddWorkflow<McpTaskDetachedParentResearchWorkflow>()
            .AddWorkflow<McpTaskDetachedExecutorResearchWorkflow>()
            .AddScopedActivities<McpTaskLifecycleResearchActivities>();
        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"mcp-task-child-research-{Guid.NewGuid():N}";
        var parent = await environment.Client.StartWorkflowAsync(
            (McpTaskDetachedParentResearchWorkflow workflow) =>
                workflow.RunAsync(new McpTaskDetachedParentInput(0, null)),
            new WorkflowOptions(workflowId, taskQueue));
        var firstParentRunId = parent.ResultRunId!;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref tracker.StartAttemptCount) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(1, tracker.StartAttemptCount);
        gate.Complete("detached report ready");

        var result = await parent.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(McpTaskLifecycleState.Completed, result.State);
        Assert.Equal("detached report ready", result.ResultText);
        Assert.Equal(1, tracker.StartAttemptCount);

        var secondParentRunId = (await parent.DescribeAsync()).RunId;
        Assert.NotEqual(firstParentRunId, secondParentRunId);
        var childId = $"{workflowId}/mcp-task-executor";

        var parentFirstHistory = await environment.Client
            .GetWorkflowHandle<McpTaskDetachedParentResearchWorkflow>(workflowId, firstParentRunId)
            .FetchHistoryAsync();
        var parentSecondHistory = await environment.Client
            .GetWorkflowHandle<McpTaskDetachedParentResearchWorkflow>(workflowId, secondParentRunId)
            .FetchHistoryAsync();
        var childHistory = await environment.Client
            .GetWorkflowHandle<McpTaskDetachedExecutorResearchWorkflow>(childId)
            .FetchHistoryAsync();

        var replayerOptions = new WorkflowReplayerOptions();
        replayerOptions.AddWorkflow<McpTaskDetachedParentResearchWorkflow>();
        replayerOptions.AddWorkflow<McpTaskDetachedExecutorResearchWorkflow>();
        var replayer = new WorkflowReplayer(replayerOptions);
        Assert.Null((await replayer.ReplayWorkflowAsync(parentFirstHistory, false)).ReplayFailure);
        Assert.Null((await replayer.ReplayWorkflowAsync(parentSecondHistory, false)).ReplayFailure);
        Assert.Null((await replayer.ReplayWorkflowAsync(childHistory, false)).ReplayFailure);

        await host.StopAsync();
    }
}

public sealed record McpTaskDetachedParentInput(int Generation, string? ExecutorWorkflowId);

[Workflow("TemporalCommunity.Extensions.AI.Tests.McpTaskDetachedParentResearchWorkflow")]
public sealed class McpTaskDetachedParentResearchWorkflow
{
    private McpTaskLifecycleResearchResult? result;

    [WorkflowRun]
    public async Task<McpTaskLifecycleResearchResult> RunAsync(McpTaskDetachedParentInput input)
    {
        if (input.Generation == 0)
        {
            var executorId = $"{Workflow.Info.WorkflowId}/mcp-task-executor";
            await Workflow.StartChildWorkflowAsync(
                (McpTaskDetachedExecutorResearchWorkflow workflow) =>
                    workflow.RunAsync(Workflow.Info.WorkflowId),
                new ChildWorkflowOptions
                {
                    Id = executorId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                });
            throw Workflow.CreateContinueAsNewException(
                Workflow.Info.WorkflowType,
                new object[] { new McpTaskDetachedParentInput(1, executorId) });
        }

        await Workflow.WaitConditionAsync(() => result is not null);
        return result!;
    }

    [WorkflowSignal("McpTaskCompleted")]
    public Task TaskCompletedAsync(McpTaskLifecycleResearchResult completed)
    {
        result = completed;
        return Task.CompletedTask;
    }
}

[Workflow("TemporalCommunity.Extensions.AI.Tests.McpTaskDetachedExecutorResearchWorkflow")]
public sealed class McpTaskDetachedExecutorResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string parentWorkflowId)
    {
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(10),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromMilliseconds(25),
                MaximumAttempts = 2,
            },
        };
        var started = await Workflow.ExecuteActivityAsync(
            (McpTaskLifecycleResearchActivities activities) => activities.StartAsync(),
            activityOptions);
        McpTaskLifecycleResearchResult terminal;
        if (!started.IsTask)
        {
            terminal = new(null, started.State, started.ResultText, null);
        }
        else
        {
            var taskId = started.TaskId!;
            var pollIntervalMs = started.PollIntervalMs;
            terminal = await PollUntilTerminalAsync(taskId, pollIntervalMs, activityOptions);
        }

        await Workflow.GetExternalWorkflowHandle<McpTaskDetachedParentResearchWorkflow>(
                parentWorkflowId)
            .SignalAsync(workflow => workflow.TaskCompletedAsync(terminal));
    }

    private static async Task<McpTaskLifecycleResearchResult> PollUntilTerminalAsync(
        string taskId,
        long pollIntervalMs,
        ActivityOptions activityOptions)
    {
        const int MaxPolls = 40;
        for (var poll = 0; poll < MaxPolls; poll++)
        {
            await Workflow.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(1, pollIntervalMs)));
            var result = await Workflow.ExecuteActivityAsync(
                (McpTaskLifecycleResearchActivities activities) =>
                    activities.PollAsync(new McpTaskPollResearchInput(taskId)),
                activityOptions);
            pollIntervalMs = result.PollIntervalMs;
            if (result.State == McpTaskLifecycleState.Working)
            {
                continue;
            }

            if (result.State == McpTaskLifecycleState.InputRequired)
            {
                return new(
                    taskId,
                    McpTaskLifecycleState.Failed,
                    null,
                    "InputRequired is intentionally unsupported by this research phase.");
            }

            return new(result.TaskId, result.State, result.ResultText, result.Error);
        }

        return new(
            taskId,
            McpTaskLifecycleState.Failed,
            null,
            $"Polling exceeded the research cap of {MaxPolls} attempts.");
    }
}
