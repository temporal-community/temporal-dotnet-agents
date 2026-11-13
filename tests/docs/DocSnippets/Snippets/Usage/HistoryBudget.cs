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
        Console.WriteLine(
            $"Events: {status.HistoryEventCount}; soft/reserve: " +
            $"{status.HistoryEventSoftLimit}/{status.HistoryEventReserveLimit}");
        Console.WriteLine(
            $"Size: {status.HistorySizeBytes} bytes; soft/reserve: " +
            $"{status.HistorySizeSoftLimitBytes}/{status.HistorySizeReserveLimitBytes} bytes");
        Console.WriteLine(
            $"Joiners: {status.ConcurrentTurnJoiners}/{status.MaxConcurrentTurnJoiners}; " +
            $"admission restricted: {status.IsAdmissionRestricted}; reserve reached: {status.IsReserveReached}");
        // END SNIPPET docs/how-to/MAF/usage.md#inspecting-workflow-history-budget
    }
}
