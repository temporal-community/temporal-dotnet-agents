// The shared test project supplies the pinned server helper and controlled attempt.
using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Client;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace DocSnippets.MEAI;

internal static class PhaseHarness
{
    internal static async Task TimerAsync()
    {
        // BEGIN SNIPPET docs/how-to/MEAI/testing.md#testing-workflow-timers
        await using var environment = await TemporalServiceTestEnvironment.StartTimeSkippingAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var queue = $"timer-example-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<TimerExampleWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (TimerExampleWorkflow workflow) => workflow.RunAsync(TimeSpan.FromDays(3)),
                new($"timer-example-{Guid.NewGuid():N}", queue));
            var elapsed = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(elapsed >= TimeSpan.FromDays(3));
        });
        // END SNIPPET docs/how-to/MEAI/testing.md#testing-workflow-timers
    }

    internal static async Task ControlledToolAsync()
    {
        // BEGIN SNIPPET docs/how-to/MEAI/testing.md#testing-activity-side-effects-across-retries
        var attempt = new ControlledActivityAttempt<string>();
        var effects = 0;
        var tool = AIFunctionFactory.Create(
            (CancellationToken token) => attempt.ExecuteAsync(
                () => Interlocked.Increment(ref effects), token),
            "controlled_write");
        var pending = tool.InvokeAsync(new AIFunctionArguments()).AsTask();
        try
        {
            await attempt.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, effects);
            Assert.False(pending.IsCompleted);
            attempt.Complete("written");
            Assert.Equal("written", Assert.IsType<JsonElement>(await pending).GetString());
        }
        finally
        {
            if (!attempt.IsCompletionSupplied)
                attempt.Complete("cleanup");
        }
        // END SNIPPET docs/how-to/MEAI/testing.md#testing-activity-side-effects-across-retries
    }
}

// BEGIN SNIPPET docs/how-to/MEAI/testing.md#testing-workflow-timers
[Workflow]
public class TimerExampleWorkflow
{
    [WorkflowRun]
    public async Task<TimeSpan> RunAsync(TimeSpan delay)
    {
        var started = Workflow.UtcNow;
        await Workflow.DelayAsync(delay);
        return Workflow.UtcNow - started;
    }
}
// END SNIPPET docs/how-to/MEAI/testing.md#testing-workflow-timers
