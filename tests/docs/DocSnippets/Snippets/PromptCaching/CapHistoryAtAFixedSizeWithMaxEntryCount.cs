// HARNESS for docs/how-to/MAF/prompt-caching.md § "5b. Cap History at a Fixed Size with MaxEntryCount".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.PromptCaching;

internal static class CapHistoryAtAFixedSizeWithMaxEntryCount
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/prompt-caching.md#5b-cap-history-at-a-fixed-size-with-maxentrycount (lines 277-287)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
                opts.DefaultMaxEntryCount = 50;  // keep at most 50 entries across continue-as-new
            });
        // END SNIPPET docs/how-to/MAF/prompt-caching.md#5b-cap-history-at-a-fixed-size-with-maxentrycount
    }
}
