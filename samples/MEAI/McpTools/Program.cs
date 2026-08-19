using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Samples.Mcp;
using Temporalio.Extensions.Hosting;

const string TaskQueue = "meai-mcp-tools";
var useDynamicDiscovery = args.Contains("--dynamic", StringComparer.Ordinal);

await using var mcp = await McpSampleConnection.CreateAsync();
IReadOnlyList<McpClientTool> tools;

if (useDynamicDiscovery)
{
    // Development/trusted-catalog mode: every currently advertised tool is exposed.
    tools = [.. await mcp.Client.ListToolsAsync()];
}
else
{
    // Production mode: checked-in schemas are the model-visible allowlist. Construction does not
    // prove that the server implementation remains compatible; invocation still can fail clearly.
    tools = [.. McpSampleConnection.LoadPinnedDefinitions("pinned-tools.json")
        .Select(definition => new McpClientTool(mcp.Client, definition))];
}

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

var worker = builder.Services
    .AddHostedTemporalWorker(TaskQueue)
    .AddDurableAI(options => options.DefaultToolsetIds = ["inventory-v1"]);

worker.AddDurableToolset("inventory-v1", set => set
    .Add(lookup)
    .Add(delete, options => options.NoRetry().RequireApproval()));

using var host = builder.Build();
await host.StartAsync();

var sessions = host.Services.GetRequiredService<IDurableChatSessionClient>();
var result = await sessions.SendAsync(
    $"mcp-{Guid.NewGuid():N}",
    [new ChatMessage(ChatRole.User, "How many SKU-123 units are available?")]);
Console.WriteLine(result.Text);

await host.StopAsync();
