// HARNESS for docs/how-to/MAF/context-providers.md § "Filter tools with InvokingCoreAsync".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ContextProviders;

// BEGIN SNIPPET docs/how-to/MAF/context-providers.md#filter-tools-with-invokingcoreasync
internal static class ProviderSelectedTool
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        var searchTool = AIFunctionFactory.Create(
            () => "Search results",
            name: "search");
        agent.AddTool(searchTool);
        agent.AddContextProvider(new SearchToolProvider());
    }

    private sealed class SearchToolProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            var selectedTools = context.AIContext.Tools?
                .Where(tool => tool.Name == "search")
                .ToArray();

            return ValueTask.FromResult(new AIContext
            {
                Instructions = context.AIContext.Instructions,
                Messages = context.AIContext.Messages,
                Tools = selectedTools,
            });
        }
    }
}
// END SNIPPET docs/how-to/MAF/context-providers.md#filter-tools-with-invokingcoreasync
