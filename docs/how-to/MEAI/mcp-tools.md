# MCP tools in durable MEAI sessions

The MCP C# SDK exposes remote tools as `McpClientTool : AIFunction`; no Temporal-specific MCP
adapter is required. Register those functions with `AddDurableTool`, `AddDurableTools`, or a named
`AddDurableToolset`. The durable workflow continues to own tool selection, approval, and per-tool
activity dispatch.

## Choose catalog authority deliberately

For a trusted development server, discover the current catalog before the worker starts:

```csharp
await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://mcp.example.com"),
    Name = "inventory",
}));

IList<McpClientTool> tools = await mcp.ListToolsAsync();
worker.AddDurableTools(tools); // all discovered tools share the default durable policy
```

The example above calls `mcp.ListToolsAsync()` against the live server every time the worker
starts — live discovery. For production or side-effecting tools, do the opposite: check reviewed
`Tool` definitions (`ModelContextProtocol.Protocol.Tool`) into source control ahead of time, and
build the `McpClientTool` wrapper objects directly from those saved definitions instead of asking
the server what tools currently exist:

```csharp
IReadOnlyList<Tool> pinned = LoadCheckedInDefinitions();
var tools = pinned
    .Select(definition => new McpClientTool(mcp, definition))
    .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

worker.AddDurableToolset("orders-v1", set => set
    .Add(tools["get_order"])
    .Add(tools["cancel_order"], policy => policy.NoRetry().RequireApproval()));
```

The exact lookup intentionally fails when a required tool is missing. Do not silently accept
renamed tools or automatically include newly advertised tools. Refresh a pinned definition through
normal code review and give the revised catalog a versioned toolset ID when it changes the durable
model surface.

## Operational and security boundaries

- Create the MCP client outside workflow code and keep it for the worker lifetime.
- Authenticate the transport. A pinned schema is not server identity, authorization, or evidence
  that the current remote implementation matches it.
- Reauthorize current authoritative state inside an effectful tool immediately before the effect.
- Treat MCP arguments and results as untrusted input; bound payloads and validate business data.
- A stale pinned tool fails when invoked unless the application performs an optional startup probe.
- Worker startup discovery couples availability to the remote server. Pinning avoids `tools/list`
  at reconnect but does not make tool invocation available while the server is down.

The [McpTools sample](../../../samples/MEAI/McpTools) uses a real in-process stream transport and a
scripted model so the topology is reproducible locally. See also the shared [security boundary](../../security.md).
