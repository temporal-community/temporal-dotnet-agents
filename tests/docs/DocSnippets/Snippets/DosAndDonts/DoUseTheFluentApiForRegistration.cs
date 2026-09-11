// HARNESS for docs/how-to/MAF/dos-and-donts.md § "Do use the fluent API for registration".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.DosAndDonts;

internal static class DoUseTheFluentApiForRegistration
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-the-fluent-api-for-registration (lines 123-133)
        // GOOD
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
            });
        // END SNIPPET docs/how-to/MAF/dos-and-donts.md#do-use-the-fluent-api-for-registration
    }
}
