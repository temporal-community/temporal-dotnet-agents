// HARNESS for docs/how-to/MAF/mcp-tools.md § "The default policy retries".
using ModelContextProtocol.Client;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.McpTools;

internal static class TheDefaultPolicyRetries
{
    internal static void Configure(
        DurableAgentBuilder agent,
        AIFunction[] reads,
        IReadOnlyDictionary<string, McpClientTool> byName)
    {
        // BEGIN SNIPPET docs/how-to/MAF/mcp-tools.md#the-default-policy-retries (lines 99-103)
        agent.AddTools(reads);                                  // lookups — retry is a feature
        agent.AddTool(byName["delete_inventory"],
            policy => policy.NoRetry().RequireApproval());      // effects — register individually
        // END SNIPPET docs/how-to/MAF/mcp-tools.md#the-default-policy-retries
    }
}
