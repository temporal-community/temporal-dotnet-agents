// HARNESS for docs/how-to/MAF/observability.md § "Querying via ListWorkflowsAsync".
using Temporalio.Client;

namespace DocSnippets.Observability;

internal static class ListWorkflows
{
    internal static async Task QueryAsync(ITemporalClient client)
    {
        // BEGIN SNIPPET docs/how-to/MAF/observability.md#querying-via-listworkflowsasync (lines 277-283)
        var result = client.ListWorkflowsAsync(
            "AgentName = 'BillingAgent' AND TurnCount > 5");

        await foreach (var execution in result)
        {
            Console.WriteLine(execution.Id);
        }
        // END SNIPPET docs/how-to/MAF/observability.md#querying-via-listworkflowsasync
    }
}
