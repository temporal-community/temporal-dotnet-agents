// HARNESS for docs/how-to/MAF/hitl-patterns.md § "Reusable session grants".
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Hitl;

internal static class ReusableSessionGrants
{
    internal static void Configure(DurableAgentBuilder agent, AIFunction writeFile)
    {
        // BEGIN SNIPPET docs/how-to/MAF/hitl-patterns.md#reusable-session-grants (lines 354-357)
        agent.AddTool(writeFile, tool => tool.NoRetry().RequireApproval().ScopeAware());
        agent.UseApprovalScopes();   // installs the built-in scope-aware interceptor
        // END SNIPPET docs/how-to/MAF/hitl-patterns.md#reusable-session-grants
    }
}
