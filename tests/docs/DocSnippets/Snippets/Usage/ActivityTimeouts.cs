// HARNESS for docs/how-to/MAF/usage.md § "Activity Timeouts".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class ActivityTimeouts
{
    internal static void Configure(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#activity-timeouts (lines 549-568)
        builder.Services.AddTemporalClient("localhost:7233", "default");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                // Increase for slow models or long tool-call chains
                opts.DefaultActivityTimeout = TimeSpan.FromMinutes(10);

                // Increase for long-running model calls
                opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(2);

                opts.AddDurableAgent("MyAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.TimeToLive = TimeSpan.FromHours(1);
                });
            });
        // END SNIPPET docs/how-to/MAF/usage.md#activity-timeouts
    }
}
