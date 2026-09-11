// HARNESS for docs/how-to/MAF/hitl-patterns.md § "Budget the approval and activity timeouts first".
//
// The block mixes worker-level option assignments with a per-agent AddTool call, so the harness
// supplies both `opts` and `agent`.
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Hitl;

internal static class BudgetTheApprovalAndActivityTimeoutsFirst
{
    internal static void Configure(TemporalAgentsOptions opts, DurableAgentBuilder agent, AIFunction publishDraft)
    {
        // BEGIN SNIPPET docs/how-to/MAF/hitl-patterns.md#budget-the-approval-and-activity-timeouts-first (lines 151-159)
        opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(20);  // outer bound on the whole review
        opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(15);  // must stay under ActivityTimeout

        // Optional: change the inherited 2-minute heartbeat cadence.
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);   // pump runs at a third of this

        agent.AddTool(publishDraft, tool => tool.NoRetry());
        // END SNIPPET docs/how-to/MAF/hitl-patterns.md#budget-the-approval-and-activity-timeouts-first
    }
}
