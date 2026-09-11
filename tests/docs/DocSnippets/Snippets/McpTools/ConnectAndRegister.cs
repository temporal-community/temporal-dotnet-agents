// HARNESS for docs/how-to/MAF/mcp-tools.md § "Connect and register".
using ModelContextProtocol.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.McpTools;

internal static class ConnectAndRegister
{
    internal static async Task ConfigureAsync(TemporalAgentsOptions options)
    {
        // BEGIN SNIPPET docs/how-to/MAF/mcp-tools.md#connect-and-register (lines 19-37)
        await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "inventory",
            Command = "inventory-mcp-server",
        }));

        IList<McpClientTool> discovered = await mcp.ListToolsAsync();
        var byName = discovered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        options.AddDurableAgent("InventoryAgent", agent =>
        {
            agent.ChatClient = services => services.GetRequiredService<IChatClient>();
            agent.AddTool(byName["lookup_inventory"]);
            agent.AddTool(
                byName["delete_inventory"],
                policy => policy.NoRetry().RequireApproval());
        });
        // END SNIPPET docs/how-to/MAF/mcp-tools.md#connect-and-register
    }
}
