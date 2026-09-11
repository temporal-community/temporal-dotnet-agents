// HARNESS for docs/how-to/MAF/prompt-caching.md § "1. Use Short-Lived Sessions".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.PromptCaching;

internal static class UseShortLivedSessions
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/prompt-caching.md#1-use-short-lived-sessions (lines 200-206)
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });
        // END SNIPPET docs/how-to/MAF/prompt-caching.md#1-use-short-lived-sessions
    }
}
