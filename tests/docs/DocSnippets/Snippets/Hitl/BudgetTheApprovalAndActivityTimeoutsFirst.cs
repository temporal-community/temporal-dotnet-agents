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
        // BEGIN SNIPPET docs/how-to/MAF/hitl-patterns.md#budget-the-approval-and-activity-timeouts-first (lines 150-156)
        opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(20);  // default start-to-close budget for the tool
        opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(15);  // leaves five minutes for other tool work

        // Optional: change the inherited 2-minute heartbeat timeout.
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);   // RequestApprovalAsync also heartbeats at ~1/3

        agent.AddTool(publishDraft);
        // END SNIPPET docs/how-to/MAF/hitl-patterns.md#budget-the-approval-and-activity-timeouts-first
    }
}
