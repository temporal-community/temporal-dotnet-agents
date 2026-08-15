using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Tests.Shared;
using TemporalCommunity.Extensions.Tests.Shared.Research;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Non-shipping research coverage. The workflow and coordinator live only in test assemblies.
/// </summary>
public sealed class DeferredToolResearchIntegrationTests
{
    [Fact]
    public async Task PendingInput_SurvivesContinueAsNew_AndBothRunsReplay()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var taskQueue = $"deferred-tool-research-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(environment.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<DeferredToolResearchWorkflow>();
        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"deferred-tool-research-{Guid.NewGuid():N}";
        var handle = await environment.Client.StartWorkflowAsync(
            (DeferredToolResearchWorkflow workflow) => workflow.RunAsync(
                new DeferredToolResearchInput(Generation: 0, Snapshot: null)),
            new WorkflowOptions(workflowId, taskQueue));
        var firstRunId = (await handle.DescribeAsync()).RunId;
        var request = new DeferredToolRequestPrototype(
            "invocation-one",
            "operator-input",
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(
            DeferredToolTransitionPrototype.Accepted,
            await handle.ExecuteUpdateAsync(
                workflow => workflow.BeginAsync(request),
                new WorkflowUpdateOptions { Id = "begin-input" }));
        await handle.SignalAsync(workflow => workflow.ContinueAsNewAsync());

        var secondRunId = await WaitForNewRunAsync(handle, firstRunId);
        var secondRunHandle = environment.Client.GetWorkflowHandle<DeferredToolResearchWorkflow>(
            workflowId,
            secondRunId);
        var restored = await secondRunHandle.QueryAsync(workflow => workflow.GetState());
        Assert.Equal(1, restored.Generation);
        Assert.Equal("invocation-one", Assert.Single(restored.Snapshot.Pending).InvocationId);
        Assert.Equal(
            DeferredToolTransitionPrototype.CapacityExceeded,
            await secondRunHandle.ExecuteUpdateAsync(
                workflow => workflow.BeginAsync(new DeferredToolRequestPrototype(
                    "invocation-two",
                    "operator-input",
                    DateTimeOffset.UtcNow.AddMinutes(5))),
                new WorkflowUpdateOptions { Id = "cap-pressure" }));

        var completion = new DeferredToolCompletionPrototype("invocation-one", "provided-value");
        Assert.Equal(
            DeferredToolTransitionPrototype.Accepted,
            await secondRunHandle.ExecuteUpdateAsync(
                workflow => workflow.SubmitAsync(completion),
                new WorkflowUpdateOptions { Id = "submit-input" }));
        Assert.Equal(
            DeferredToolTransitionPrototype.AlreadyResolved,
            await secondRunHandle.ExecuteUpdateAsync(
                workflow => workflow.SubmitAsync(completion),
                new WorkflowUpdateOptions { Id = "submit-input-duplicate" }));
        var conflictingCompletion = completion with { Payload = "different" };
        Assert.Equal(
            DeferredToolTransitionPrototype.Conflict,
            await secondRunHandle.ExecuteUpdateAsync(
                workflow => workflow.SubmitAsync(conflictingCompletion),
                new WorkflowUpdateOptions { Id = "submit-input-conflict" }));

        await secondRunHandle.SignalAsync(workflow => workflow.ShutdownAsync());
        await secondRunHandle.GetResultAsync();

        var firstHistory = await environment.Client
            .GetWorkflowHandle<DeferredToolResearchWorkflow>(workflowId, firstRunId)
            .FetchHistoryAsync();
        var secondHistory = await secondRunHandle.FetchHistoryAsync();
        var replayerOptions = new WorkflowReplayerOptions();
        replayerOptions.AddWorkflow<DeferredToolResearchWorkflow>();
        var replayer = new WorkflowReplayer(replayerOptions);

        Assert.Null((await replayer.ReplayWorkflowAsync(firstHistory, false)).ReplayFailure);
        Assert.Null((await replayer.ReplayWorkflowAsync(secondHistory, false)).ReplayFailure);

        await host.StopAsync();
    }

    private static async Task<string> WaitForNewRunAsync(
        WorkflowHandle<DeferredToolResearchWorkflow> handle,
        string firstRunId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var currentRunId = (await handle.DescribeAsync()).RunId;
            if (!string.Equals(currentRunId, firstRunId, StringComparison.Ordinal))
            {
                return currentRunId;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The research workflow did not continue as new.");
    }
}

public sealed record DeferredToolResearchInput(
    int Generation,
    DeferredToolCoordinatorSnapshotPrototype? Snapshot);

public sealed record DeferredToolResearchState(
    int Generation,
    DeferredToolCoordinatorSnapshotPrototype Snapshot);

[Workflow("TemporalCommunity.Extensions.AI.Tests.DeferredToolResearchWorkflow")]
public sealed class DeferredToolResearchWorkflow
{
    private DeferredToolCoordinatorPrototype _coordinator = new();
    private int _generation;
    private bool _continueAsNew;
    private bool _shutdown;

    [WorkflowRun]
    public async Task RunAsync(DeferredToolResearchInput input)
    {
        _generation = input.Generation;
        _coordinator = input.Snapshot is null
            ? new DeferredToolCoordinatorPrototype()
            : DeferredToolCoordinatorPrototype.Restore(input.Snapshot);
        await Workflow.WaitConditionAsync(() => _continueAsNew || _shutdown);
        if (_continueAsNew)
        {
            throw Workflow.CreateContinueAsNewException(
                Workflow.Info.WorkflowType,
                new object[]
                {
                    new DeferredToolResearchInput(_generation + 1, _coordinator.Capture()),
                });
        }
    }

    [WorkflowUpdate("Begin")]
    public Task<DeferredToolTransitionPrototype> BeginAsync(DeferredToolRequestPrototype request) =>
        Task.FromResult(_coordinator.Begin(request, Workflow.UtcNow));

    [WorkflowUpdate("Submit")]
    public Task<DeferredToolTransitionPrototype> SubmitAsync(DeferredToolCompletionPrototype completion) =>
        Task.FromResult(_coordinator.Submit(completion, Workflow.UtcNow));

    [WorkflowQuery("State")]
    public DeferredToolResearchState GetState() => new(_generation, _coordinator.Capture());

    [WorkflowSignal("ContinueAsNew")]
    public Task ContinueAsNewAsync()
    {
        _continueAsNew = true;
        return Task.CompletedTask;
    }

    [WorkflowSignal("Shutdown")]
    public Task ShutdownAsync()
    {
        _shutdown = true;
        return Task.CompletedTask;
    }
}
