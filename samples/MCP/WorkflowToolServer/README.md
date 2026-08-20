# Authenticated workflow-backed MCP tools

This sample exposes ordinary MCP tools over Streamable HTTP. Each authorized call starts or joins a
Temporal Workflow and returns its business result. It adds no TemporalCommunity MCP adapter and does
not use the negotiated MCP Tasks extension.

```bash
temporal server start-dev
dotnet run --project samples/MCP/WorkflowToolServer --urls http://127.0.0.1:5100
```

The sample-only bearer format is `sample:<tenant>:writer`, for example:

```text
Authorization: Bearer sample:tenant-a:writer
```

Use real OIDC/JWT authentication in production. The endpoint requires authentication,
`AddAuthorizationFilters()` honors the tool's `[Authorize]` policy before execution, and the tool
rechecks the authenticated tenant and scope immediately before starting durable work.

## Two explicit operations

- `start_unique_operation` rejects any running or retained duplicate.
- `start_or_join_operation` joins a running execution, awaits its business result, and recovers the
  same completed result on retry. Failed, canceled, timed-out, or terminated executions map to a
  tenant-safe failure instead of a false “joined” success.

| Outcome | Public status | Public error code |
|---|---|---|
| Application failure | `failed` | `operation_failed` |
| Cancellation | `failed` | `operation_canceled` |
| Timeout | `failed` | `operation_timed_out` |
| Termination | `failed` | `operation_terminated` |

Both tools advertise a JSON output schema and return the same tenant-safe result in two MCP channels:

- `structuredContent` contains `operationId`, `status`, `result`, and `errorCode`;
- text `content` contains the equivalent JSON for clients that do not consume structured output.

Completed operations return `isError: false`. Conflicts and closed workflow failures return
`isError: true` with their stable public error code, so an MCP client or model can distinguish a
business failure from a successful tool execution without parsing exception text. Protocol errors
such as an unknown tool or malformed request remain MCP protocol errors.

```json
{
  "operationId": "refresh-2026-08-19",
  "status": "completed",
  "result": "inventory-refresh completed",
  "errorCode": null
}
```

Callers supply an application operation ID, never a Temporal workflow ID. The server derives an
opaque tenant-scoped workflow ID and does not return it. Request cancellation stops that caller's
wait; it does not cancel the durable workflow.

The in-memory ledger is demonstration code. Temporal workflow-ID reuse protects only while the
execution remains retained. Production idempotency that must outlive Temporal retention requires an
atomic, durable application ledger keyed by authenticated tenant and operation ID. That ledger must
store completed results or conflicts before retention expires.

MCP Tasks are a separate experimental lifecycle; see the
[research ADR](../../../docs/architecture/MCP/durable-mcp-task-research.md).
The full security and lifecycle contract is in the
[workflow-backed MCP server guide](../../../docs/how-to/MCP/workflow-tool-server.md).
