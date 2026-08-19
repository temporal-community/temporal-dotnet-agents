using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Samples.Mcp;
using Temporalio.Extensions.Hosting;

const string TaskQueue = "maf-mcp-tools";
var useDynamicDiscovery = args.Contains("--dynamic", StringComparer.Ordinal);

await using var mcp = await McpSampleConnection.CreateAsync();
IReadOnlyList<McpClientTool> tools = useDynamicDiscovery
    ? [.. await mcp.Client.ListToolsAsync()]
    : [.. McpSampleConnection.LoadPinnedDefinitions("pinned-tools.json")
        .Select(definition => new McpClientTool(mcp.Client, definition))];

var byName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
var lookup = byName.TryGetValue("lookup_inventory", out var readTool)
    ? readTool
    : throw new InvalidOperationException("Required MCP tool 'lookup_inventory' is missing.");
var delete = byName.TryGetValue("delete_inventory", out var writeTool)
    ? writeTool
    : throw new InvalidOperationException("Required MCP tool 'delete_inventory' is missing.");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddChatClient(new McpSampleChatClient());

builder.Services
    .AddHostedTemporalWorker(TaskQueue)
    .AddTemporalAgents(options => options.AddDurableAgent("InventoryAgent", agent =>
    {
        agent.Instructions = "Use lookup_inventory for inventory questions.";
        agent.ChatClient = services => services.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
        agent.AddTool(lookup);
        agent.AddTool(delete, policy => policy.NoRetry().RequireApproval());
    }));

using var host = builder.Build();
await host.StartAsync();

var proxy = host.Services.GetTemporalAgentProxy("InventoryAgent");
var session = await proxy.CreateSessionAsync();
var result = await proxy.RunAsync("How many SKU-123 units are available?", session);
Console.WriteLine(result.Text);

if (session is TemporalAgentSession temporalSession)
{
    await host.Services.GetRequiredService<ITemporalAgentClient>()
        .ShutdownAsync(temporalSession.SessionId);
}

await host.StopAsync();
