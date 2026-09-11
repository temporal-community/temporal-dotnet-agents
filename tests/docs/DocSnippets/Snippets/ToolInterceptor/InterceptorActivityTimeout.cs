// HARNESS for docs/how-to/MAF/tool-interceptor.md § "Interceptor activity timeout".
//
// FORMER DOC DEFECT (fixed in the doc; kept as a regression note): factory lambda passed first. The doc now matches this snippet verbatim.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ToolInterceptor;

internal static class InterceptorActivityTimeout
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        // BEGIN SNIPPET docs/how-to/MAF/tool-interceptor.md#interceptor-activity-timeout (lines 338-346)
        agent.AddTool(
            "write_record",
            sp => AIFunctionFactory.Create(
                sp.GetRequiredService<DataService>().WriteRecord, name: "write_record"),
            opts => opts
                .NoRetry()
                .WithInterceptorTimeout(TimeSpan.FromSeconds(10)));  // interceptor gets 10s
                // tool's own StartToCloseTimeout still falls through agent.ActivityTimeout
                // to opts.DefaultActivityTimeout
        // END SNIPPET docs/how-to/MAF/tool-interceptor.md#interceptor-activity-timeout
    }
}
