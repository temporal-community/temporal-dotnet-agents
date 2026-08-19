# MCP tools in durable MAF agents

`McpClientTool` derives from MEAI `AIFunction`, so register it with the existing MAF `AddTool` or
`AddTools` methods. Agent middleware remains responsible for model-step concerns; the durable
workflow—not MCP or MAF function middleware—dispatches each function in a separate activity.

Use async startup code and a worker-lifetime client:

```csharp
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
```

For controlled development catalogs, `agent.AddTools(discovered)` exposes all returned tools with
the default policy. For production allowlists, deserialize reviewed `Protocol.Tool` definitions,
construct `new McpClientTool(mcp, definition)`, and select sensitive names exactly with
`StringComparer.Ordinal`. A pinned definition controls the schema shown to the model, but does not
authenticate the server or prove runtime compatibility.

Keep MCP clients out of agent session state and workflow state. Dispose them after the worker
stops. Authenticate the transport, authorize the application principal before durable work, and
reauthorize immediately before an effect. See the runnable [McpTools sample](../../../samples/MAF/McpTools)
and shared [security boundary](../../security.md).
