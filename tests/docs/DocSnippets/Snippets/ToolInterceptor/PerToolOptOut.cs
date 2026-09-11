// HARNESS for docs/how-to/MAF/tool-interceptor.md § "Per-tool opt-out".
//
// DOC DEFECT (as of this file's commit): factory lambda passed first. Corrected below.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ToolInterceptor;

internal static class PerToolOptOut
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        // BEGIN SNIPPET docs/how-to/MAF/tool-interceptor.md#per-tool-opt-out (lines 321-326)
        agent.AddTool(
            "search_products",
            sp => AIFunctionFactory.Create(
                sp.GetRequiredService<CatalogService>().SearchProducts, name: "search_products"),
            opts => opts.SkipInterceptor());
        // END SNIPPET docs/how-to/MAF/tool-interceptor.md#per-tool-opt-out
    }
}
