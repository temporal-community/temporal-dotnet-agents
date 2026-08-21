# Durable MAF tools from MCP

This sample registers MCP SDK `McpClientTool` instances as ordinary MAF tools. The durable agent
workflow owns function dispatch, so every model step and MCP invocation is a separate Temporal
activity. The MCP client remains in the worker process and is disposed after the worker host stops.

```bash
dotnet run --project samples/MAF/McpTools
dotnet run --project samples/MAF/McpTools -- --dynamic
```

The default mode builds tools from checked-in `Protocol.Tool` definitions. That avoids live
`tools/list` changing the model surface, but it neither authenticates the MCP server nor proves its
implementation is compatible. Exact ordinal lookup fails startup when a required sensitive tool is
missing. `delete_inventory` is registered explicitly with `NoRetry` and workflow-parked approval;
the server's unexpected `server_admin` tool is excluded.

## Prerequisites

- .NET 10 SDK;
- a local Temporal service at `localhost:7233` (for example, `temporal server start-dev`);
- no model API key—the sample uses a deterministic in-process chat client;
- no external MCP service—the sample MCP transport and server run in-process.

Ownership:

- the application process owns the MCP connection and Temporal worker;
- MAF middleware owns model-step concerns, not function invocation;
- the workflow owns approval and durable tool dispatch;
- the tool activity invokes MCP and must reauthorize current authoritative state before effects;
- Temporal history owns durable session state.

See [MCP tool guidance](../../../docs/how-to/MAF/mcp-tools.md) and
[the security boundary](../../../docs/security.md).
