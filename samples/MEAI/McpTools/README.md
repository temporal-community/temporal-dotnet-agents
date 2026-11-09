# Durable MEAI tools from MCP

This sample connects an MCP client to an in-process MCP server, then registers the returned
`McpClientTool` objects through the existing durable-tool APIs. The workflow owns orchestration;
each model step and MCP call is a separate Temporal activity. The MCP connection is worker-owned,
never serialized into workflow state, and disposed only after the worker stops.

Run the production-style pinned catalog:

```bash
dotnet run --project samples/MEAI/McpTools
```

Run trusted/development discovery:

```bash
dotnet run --project samples/MEAI/McpTools -- --dynamic
```

Pinned `Protocol.Tool` definitions are model-schema authority, not server authentication or proof
that an implementation remains compatible. Refresh them through review, authenticate the MCP
transport, require exact ordinal names for sensitive tools, and apply explicit durable policy.
Both modes explicitly register only `lookup_inventory` and `delete_inventory`; the unexpected
`server_admin` tool is not exposed in either mode. Dynamic mode obtains live schemas from the
trusted server, while pinned mode uses reviewed, checked-in schemas. Here `delete_inventory` is
`NoRetry` and requires workflow-parked approval, but this run requests only the lookup, not deletion
or approval. Discovery alone does not register every advertised tool.

## Prerequisites

- .NET 10 SDK;
- a local Temporal service at `localhost:7233` (for example, `temporal server start-dev`);
- no model API key—the sample uses a deterministic in-process chat client;
- no external MCP service—the sample MCP transport and server run in-process.

Set `TEMPORAL_ADDRESS` to target a different local endpoint (namespace `default`). The sample
shuts down its chat session after printing the completed result.

Ownership:

- the application process owns the Temporal worker and MCP connection;
- the durable workflow owns model/tool orchestration and approval waits;
- model and MCP tool calls execute in separate activities;
- the application authenticates the MCP transport and authorizes effects at execution time;
- Temporal workflow history owns durable conversation and approval state.

See [MCP tool guidance](../../../docs/how-to/MEAI/mcp-tools.md) and
[the security boundary](../../../docs/security.md).
