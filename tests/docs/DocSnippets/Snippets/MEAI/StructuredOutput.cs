using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.MEAI;

internal static class StructuredOutput
{
    internal static async Task SendAsync(IDurableChatSessionClient sessionClient)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#send-schema-bound-structured-output
        var requestId = Guid.NewGuid().ToString("N"); // Persist before submission if retries are possible.
        var response = await sessionClient.SendStructuredAsync<WeatherSummary>(
            "customer-42",
            "Return a JSON object with the weather summary.",
            requestId: requestId);

        Console.WriteLine(response.Result.Summary);
        Console.WriteLine(response.ResponseId);
        // END SNIPPET docs/how-to/MEAI/usage.md#send-schema-bound-structured-output
    }
}

public sealed class WeatherSummary
{
    public string Summary { get; set; } = string.Empty;
}
