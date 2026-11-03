// HARNESS for docs/how-to/MAF/usage.md § "Inspecting workflow history budget".
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.Usage;

internal static class HistoryBudget
{
    internal static async Task InspectAsync(
        ITemporalAgentClient client,
        TemporalAgentSessionId sessionId)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#inspecting-workflow-history-budget
        var status = await client.GetHistoryBudgetAsync(sessionId);
        Console.WriteLine($"History events: {status.HistoryEventCount}/{status.HistoryEventSoftLimit}");
        // END SNIPPET docs/how-to/MAF/usage.md#inspecting-workflow-history-budget
    }
}
