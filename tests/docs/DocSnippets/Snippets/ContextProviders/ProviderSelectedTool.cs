// HARNESS for docs/how-to/MAF/context-providers.md § "If your provider contributes tools".
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ContextProviders;

internal static class ProviderSelectedTool
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        var searchTool = AIFunctionFactory.Create(() => "memory");
        // BEGIN SNIPPET docs/how-to/MAF/context-providers.md#if-your-provider-contributes-tools (lines 261-261)
        agent.AddTool(searchTool);
        // END SNIPPET docs/how-to/MAF/context-providers.md#if-your-provider-contributes-tools
    }
}
