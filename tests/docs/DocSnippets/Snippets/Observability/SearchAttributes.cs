// HARNESS for docs/how-to/MAF/observability.md § "Search Attributes".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Observability;

internal static class SearchAttributes
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/observability.md#search-attributes (lines 246-256)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
                // opts.EnableSearchAttributes = false; // explicit opt-out
            });
        // END SNIPPET docs/how-to/MAF/observability.md#search-attributes
    }
}
