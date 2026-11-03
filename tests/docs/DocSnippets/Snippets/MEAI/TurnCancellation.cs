using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.MEAI;

internal static class TurnCancellation
{
    internal static async Task CancelAsync(DurableChatSessionClient sessionClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#cancel-an-active-turn
        var requestId = Guid.NewGuid().ToString("N"); // Persist before submission.
        var turn = sessionClient.SendAsync(
            "customer-42",
            [new ChatMessage(ChatRole.User, "Run the long operation.")],
            requestId: requestId);

        var cancellation = await sessionClient.CancelTurnAsync("customer-42", requestId);
        if (cancellation.Status == DurableTurnCancellationStatus.CancellationRequested)
        {
            Console.WriteLine("The workflow requested cancellation.");
        }
        // END SNIPPET docs/how-to/MEAI/usage.md#cancel-an-active-turn
        _ = turn;
    }
}
