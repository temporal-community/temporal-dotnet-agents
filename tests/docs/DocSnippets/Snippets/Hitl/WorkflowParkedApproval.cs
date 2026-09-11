// HARNESS for docs/how-to/MAF/hitl-patterns.md § "Workflow-parked approval".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Hitl;

internal static class WorkflowParkedApproval
{
    internal static void Configure(TemporalAgentsOptions options, AIFunction sendEmail)
    {
        // BEGIN SNIPPET docs/how-to/MAF/hitl-patterns.md#workflow-parked-approval (lines 129-135)
        options.AddDurableAgent("Operations", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(sendEmail, tool => tool.NoRetry().RequireApproval());
        });
        // END SNIPPET docs/how-to/MAF/hitl-patterns.md#workflow-parked-approval
    }
}
