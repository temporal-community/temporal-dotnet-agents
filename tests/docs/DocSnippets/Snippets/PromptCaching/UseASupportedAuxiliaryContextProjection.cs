// HARNESS for docs/how-to/MAF/prompt-caching.md § "3. Use a Supported Auxiliary Context Projection".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.PromptCaching;

internal static class UseASupportedAuxiliaryContextProjection
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/prompt-caching.md#3-use-a-supported-auxiliary-context-projection (lines 246-252)
        opts.AddDurableAgent("CodingAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.AddContextProvider(new WorkingSetContextProvider());
        });
        // END SNIPPET docs/how-to/MAF/prompt-caching.md#3-use-a-supported-auxiliary-context-projection
    }
}
