// HARNESS for docs/how-to/MAF/prompt-caching.md § "5. Reduce Retained History at Continue-as-New".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.PromptCaching;

internal static class ReduceHistoryAtContinueAsNewThreshold
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/prompt-caching.md#5-reduce-retained-history-at-continue-as-new
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
                opts.DefaultMaxEntryCount = 50;  // start history rollover handling at 50 entries
            });
        // END SNIPPET docs/how-to/MAF/prompt-caching.md#5-reduce-retained-history-at-continue-as-new
    }
}
