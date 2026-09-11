// HARNESS for docs/how-to/MAF/tool-interceptor.md § "Per-tool opt-out".
//
// FORMER DOC DEFECT (fixed in the doc; kept as a regression note): factory lambda passed first. The doc now matches this snippet verbatim.
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
