using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace DocSnippets.MEAI;

internal static class SessionRecovery
{
    internal static async Task SendAsync(DurableChatSessionClient sessionClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#recovering-a-generated-request-id
        try
        {
            await sessionClient.SendAsync("customer-42", "What is the weather in Seattle?");
        }
        catch (DurableChatRequestException exception)
        {
            var requestId = exception.RequestId; // Persist it before retrying the same input.
            Console.WriteLine(requestId);
            throw;
        }
        // END SNIPPET docs/how-to/MEAI/usage.md#recovering-a-generated-request-id
    }

    internal static async Task SendWithExpectedEpochAsync(DurableChatSessionClient sessionClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#session-lifetime-and-lineage
        var epoch = await sessionClient.GetSessionEpochAsync("customer-42");
        var response = await sessionClient.SendAsync(
            "customer-42",
            "Continue the same conversation.",
            requestId: "turn-17",
            expectedSessionEpoch: epoch);
        Console.WriteLine(response.Text);
        // END SNIPPET docs/how-to/MEAI/usage.md#session-lifetime-and-lineage
    }
}
