// HARNESS for docs/how-to/MAF/context-providers.md § "Inject context into every LLM call".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ContextProviders;

internal static class InjectContextIntoEveryLlmCall
{
    internal static void Configure(TemporalAgentsOptions opts)
    {
        // BEGIN SNIPPET docs/how-to/MAF/context-providers.md#inject-context-into-every-llm-call (lines 41-47)
        opts.AddDurableAgent("TaskAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.AddContextProvider(sp => new DateTimeProvider());
        });
        // END SNIPPET docs/how-to/MAF/context-providers.md#inject-context-into-every-llm-call
    }
}
