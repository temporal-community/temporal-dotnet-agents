using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace DocSnippets.MEAI;

internal static class RetainedHistoryIdentity
{
    internal static async Task InspectAsync(DurableChatSessionClient sessionClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#retained-history-identities
        // Inspect the stable identities associated with retained request/response pairs.
        var history = await sessionClient.GetHistoryAsync("customer-42");
        foreach (var turn in history
            .OfType<DurableSessionRequest>()
            .Where(request => request.EntryId is not null && request.TurnId is not null))
        {
            var response = history
                .OfType<DurableSessionResponse>()
                .FirstOrDefault(entry => entry.TurnId == turn.TurnId);
            Console.WriteLine($"{turn.TurnId}: {turn.EntryId} -> {response?.EntryId}");
        }
        // END SNIPPET docs/how-to/MEAI/usage.md#retained-history-identities
    }
}
