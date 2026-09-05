using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

/// <summary>
/// Temporal activities for <see cref="ResearchWorkflow"/>. A hand-written Activity is the
/// recommended way to make a single LLM call durable from a fully custom workflow — no
/// durable-adapter ceremony, just constructor-injected <see cref="IChatClient"/> and standard
/// Temporal activity dispatch. See samples/MEAI/CustomWorkflow's ShoppingActivities for the same
/// pattern used with an inline tool-invocation loop.
/// </summary>
internal sealed class ResearchActivities(
    IChatClient chatClient,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<ResearchActivities>();

    /// <summary>
    /// Summarizes a weather report for the given city via a single, non-streaming LLM call.
    /// </summary>
    [Activity("DirectAdapters.SummarizeWeather")]
    public async Task<string> SummarizeWeatherAsync(string city, string weatherReport)
    {
        _logger.LogDebug("Summarizing weather report for {City}", city);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"Summarize this weather report for {city}: {weatherReport}"),
        };

        var response = await chatClient.GetResponseAsync(messages).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }
}
