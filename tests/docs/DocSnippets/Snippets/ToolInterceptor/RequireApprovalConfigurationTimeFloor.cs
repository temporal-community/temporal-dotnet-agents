// HARNESS for docs/how-to/MAF/tool-interceptor.md § "RequireApproval — the configuration-time floor".
//
// DOC DEFECT (as of this file's commit): the doc passes the factory lambda as AddTool's first
// argument. The factory overload takes the name first. Corrected below.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using DocSnippets.Harness;

namespace DocSnippets.ToolInterceptor;

internal static class RequireApprovalConfigurationTimeFloor
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        // BEGIN SNIPPET docs/how-to/MAF/tool-interceptor.md#requireapproval--the-configuration-time-floor (lines 160-166)
        agent.AddTool(
            "delete_records",
            sp => AIFunctionFactory.Create(
                sp.GetRequiredService<DataService>().DeleteRecords,
                name: "delete_records"),
            opts => opts.RequireApproval());
        // END SNIPPET docs/how-to/MAF/tool-interceptor.md#requireapproval--the-configuration-time-floor
    }
}
