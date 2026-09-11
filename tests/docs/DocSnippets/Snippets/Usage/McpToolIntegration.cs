// HARNESS for docs/how-to/MAF/usage.md § "MCP Tool Integration".
using ModelContextProtocol.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class McpToolIntegration
{
    internal static async Task ConfigureAsync(HostApplicationBuilder builder)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#mcp-tool-integration (lines 808-837)
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri("https://mcp.example.com"),
            Name = "inventory",
        }));

        IList<McpClientTool> discovered = await mcp.ListToolsAsync();

        var byName = discovered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var lookup = byName.TryGetValue("lookup_inventory", out var read)
            ? read
            : throw new InvalidOperationException("Required MCP tool is missing.");
        var delete = byName.TryGetValue("delete_inventory", out var write)
            ? write
            : throw new InvalidOperationException("Required MCP tool is missing.");

        builder.Services
            .AddHostedTemporalWorker("agents")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("McpAgent", agent =>
                {
                    agent.Instructions = "You can call the configured MCP tools.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(lookup);
                    agent.AddTool(delete, policy => policy.NoRetry().RequireApproval());
                });
            });
        // END SNIPPET docs/how-to/MAF/usage.md#mcp-tool-integration
    }
}
