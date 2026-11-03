using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace DocSnippets.MEAI;

internal static class InterruptedTurnHistory
{
    internal static async Task InspectAsync(
        DurableChatSessionClient sessionClient,
        Task<DurableSessionResponse> turn,
        string requestId)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#inspect-interrupted-history
        try
        {
            await turn;
        }
        catch
        {
            var interrupted = (await sessionClient.GetHistoryAsync("customer-42"))
                .OfType<DurableSessionResponse>()
                .LastOrDefault(entry =>
                    entry.RequestId == requestId
                    && entry.CompletionReason == DurableTurnCompletionReason.Interrupted);
            if (interrupted is not null)
            {
                Console.WriteLine("The turn was interrupted after a tool dispatch; reconcile its effects.");
            }

            throw;
        }
        // END SNIPPET docs/how-to/MEAI/usage.md#inspect-interrupted-history
    }
}
