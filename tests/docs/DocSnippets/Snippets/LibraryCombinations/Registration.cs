// HARNESS for docs/library-combinations.md § "Registration".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.LibraryCombinations;

internal static class Registration
{
    internal static void Configure(HostApplicationBuilder builder, IChatClient chatClient)
    {
        // BEGIN SNIPPET docs/library-combinations.md#registration
        builder.Services.AddChatClient(chatClient);

        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(agents =>
                agents.Agent(
                    "WeatherAgent",
                    new ChatClientAgentOptions
                    {
                        Description = "Handles weather queries and forecasts.",
                        ChatOptions = new ChatOptions
                        {
                            Instructions = "You are a weather specialist.",
                        },
                    },
                    temporal => temporal.TimeToLive = TimeSpan.FromHours(24)));
        // END SNIPPET docs/library-combinations.md#registration
    }
}
