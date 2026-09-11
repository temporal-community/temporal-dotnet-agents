// HARNESS for docs/how-to/MAF/working-set.md § "Working set provider" (the intro block, which sits
// under the H1 — hence the key's slug is the H1's).
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.WorkingSet;

internal static class WorkingSetProvider
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/working-set.md#working-set-provider (lines 12-18)
        opts.AddDurableAgent("CodingAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.AddContextProvider(new WorkingSetContextProvider());
        });
        // END SNIPPET docs/how-to/MAF/working-set.md#working-set-provider
    }
}
