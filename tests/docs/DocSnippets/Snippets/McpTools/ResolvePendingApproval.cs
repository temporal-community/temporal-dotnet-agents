// HARNESS for docs/how-to/MAF/mcp-tools.md § "Approval".
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI.Approvals;

namespace DocSnippets.McpTools;

internal static class ResolvePendingApproval
{
    internal static async Task ResolveAsync(
        ITemporalAgentClient client,
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
        if (pending is not null)
        {
            await client.ResolveApprovalAsync(
                sessionId,
                new DurableApprovalDecision
                {
                    RequestId = pending.RequestId,   // required — ties the answer to the request
                    CallId = pending.CallId,
                    FunctionName = pending.FunctionName,
                    Approved = true,
                },
                cancellationToken);
        }
    }
}
