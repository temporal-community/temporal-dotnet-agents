// HARNESS for docs/how-to/MAF/usage.md § "Session TTL".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class SessionTtl
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#session-ttl (lines 521-530)
        opts.AddDurableAgent("ShortLivedAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });

        // Or configure the default for all agents on this worker
        opts.DefaultTimeToLive = TimeSpan.FromDays(7);
        // END SNIPPET docs/how-to/MAF/usage.md#session-ttl
    }
}
