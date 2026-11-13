// HARNESS for docs/how-to/MAF/mcp-tools.md § "Tool retries are opt-in".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.McpTools;

internal static class TheDefaultPolicyRetries
{
    internal static void Configure(
        TemporalAgentsOptions agents,
        AIFunction[] reads,
        IReadOnlyDictionary<string, AIFunction> byName)
    {
        // BEGIN SNIPPET docs/how-to/MAF/mcp-tools.md#tool-retries-are-opt-in (lines 106-114)
        agents.Agent(
            "InventoryAgent",
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = [.. reads, byName["delete_inventory"]],
                },
            });
        // END SNIPPET docs/how-to/MAF/mcp-tools.md#tool-retries-are-opt-in
    }
}
