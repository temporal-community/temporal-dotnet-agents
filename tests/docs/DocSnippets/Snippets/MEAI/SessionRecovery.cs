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
}
