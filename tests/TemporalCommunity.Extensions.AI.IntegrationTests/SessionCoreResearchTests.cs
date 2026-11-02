using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>Synthetic scheduling research; does not exercise the production session core.</summary>
public class SessionCoreResearchTests
{
    [Theory]
    [InlineData(false, "uninitialized")]
    [InlineData(true, "initialized")]
    public async Task UpdateDuringInitialization_ReproducesRaceAndDeterministicBarrier(
        bool waitForInitialization, string expected)
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"session-core-research-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<InitializationProbe>());

        await worker.ExecuteAsync(async () =>
        {
            var start = WithStartWorkflowOperation.Create(
                (InitializationProbe workflow) => workflow.RunAsync(),
                new WorkflowOptions($"initialization-{Guid.NewGuid():N}", taskQueue)
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                });
            var result = await environment.Client
                .ExecuteUpdateWithStartWorkflowAsync<InitializationProbe, string>(
                    workflow => workflow.ReadAsync(waitForInitialization),
                    new WorkflowUpdateWithStartOptions(start))
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(expected, result);
            var handle = await start.GetHandleAsync();
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var replay = await new WorkflowReplayer(new WorkflowReplayerOptions
                {
                    DataConverter = DurableAIDataConverter.Instance,
                }.AddWorkflow<InitializationProbe>())
                .ReplayWorkflowAsync(await handle.FetchHistoryAsync(), throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
        });
    }

    [Fact(Skip = "Unit 2.2: production redesigned session-core initialization/admission barrier is not implemented. Synthetic race below is research only.")]
    public Task RedesignedSessionCore_UpdateWithStart_CannotReadUninitializedState() =>
        UpdateDuringInitialization_ReproducesRaceAndDeterministicBarrier(false, "initialized");

    [Workflow]
    public class InitializationProbe
    {
        private bool _handlerEntered;
        private bool _handlerFinished;
        private string? _state;

        [WorkflowRun]
        public async Task RunAsync()
        {
            await Workflow.WaitConditionAsync(() => _handlerEntered);
            _state = "initialized";
            await Workflow.WaitConditionAsync(() => _handlerFinished && Workflow.AllHandlersFinished);
        }

        [WorkflowUpdate]
        public async Task<string> ReadAsync(bool waitForInitialization)
        {
            _handlerEntered = true;
            if (waitForInitialization)
                await Workflow.WaitConditionAsync(() => _state is not null);
            var result = _state ?? "uninitialized";
            _handlerFinished = true;
            return result;
        }
    }
}
