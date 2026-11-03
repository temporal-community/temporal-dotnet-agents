using System.Diagnostics;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Workflows;

namespace DocSnippets.Observability;

internal static class WorkflowDiagnostic
{
    internal static void Compile()
    {
        _ = typeof(DiagnosticWorkflow);
    }

    // BEGIN SNIPPET docs/how-to/MEAI/observability.md#workflow-diagnostic
    [Workflow]
    public class DiagnosticWorkflow
    {
        private static readonly ActivitySource Diagnostics = new("MyApp.DurableWorkflow");

        [WorkflowRun]
        public async Task RunAsync()
        {
            using (Diagnostics.TrackWorkflowDiagnosticActivity("durable_chat.model_step"))
            {
                await Workflow.ExecuteActivityAsync(
                    (MyActivities activity) => activity.DoThingAsync(),
                    new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
            }
        }
    }
    // END SNIPPET docs/how-to/MEAI/observability.md#workflow-diagnostic

    public sealed class MyActivities
    {
        [Temporalio.Activities.Activity]
        public Task DoThingAsync() => Task.CompletedTask;
    }
}
