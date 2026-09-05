# Workflow-backed ordinary MCP tools

An ordinary MCP server tool can call `ITemporalClient` directly. This composition is sufficient
when an MCP request starts durable application work and waits for its business result. It does not
need a `TemporalCommunity.Extensions.*` adapter. It is distinct from the MCP Tasks extension and
from the deferred Temporal-backed MCP Task integration.

The runnable [WorkflowToolServer sample](../../../samples/MCP/WorkflowToolServer) demonstrates this
topology:

```text
authenticated MCP client
    -> ASP.NET authentication
    -> MCP endpoint authorization
    -> tool-level authorization filter
    -> effect-time tenant/scope recheck
    -> ITemporalClient.StartWorkflowAsync
    -> Temporal worker
    -> business result or tenant-safe terminal error
```

## Identity and authorization

The client sends an application-owned operation ID. It does not select a Temporal workflow ID. The
server combines the authenticated tenant and operation ID with a versioned, length-prefixed SHA-256
mapping to derive an opaque workflow ID. That ID is routing data, is never returned as authority,
and is not a substitute for authorization.

The sample enforces authorization at four boundaries:

1. the MCP endpoint requires authentication;
2. the MCP SDK's `AddAuthorizationFilters()` runs the tool's policy before the method;
3. the tool rechecks the authenticated principal immediately before durable work starts; and
4. real effect-carrying workflow activities must reauthorize against current authoritative data.

The transparent `sample:<tenant>:writer` token is demonstration code. Deployments must use real
OIDC/JWT validation and must not trust tenant IDs supplied as tool arguments.

## Retry contracts

The sample deliberately exposes two different operations:

| Operation | Running duplicate | Completed retry | Closed failure |
|---|---|---|---|
| `start_unique_operation` | conflict | conflict | conflict |
| `start_or_join_operation` | await the same result | return retained result | tenant-safe failed/canceled/timed-out/terminated result |

`WorkflowIdConflictPolicy.UseExisting` returns a result-bearing handle when the execution is still
running. The server awaits `GetResultAsync`; it does not report a successful join before the
workflow completes. If a closed execution produces `WorkflowAlreadyStartedException`, the server
retrieves that execution's handle and obtains its terminal result. Raw run IDs, stack traces, and
failure details are not returned.

## MCP result semantics

The sample's application service returns a protocol-neutral `WorkflowToolResult`. The MCP adapter
maps that result to `CallToolResult` and advertises `WorkflowToolResult` as the tool output schema.
It populates both `structuredContent` and JSON text `content` from the same result object.

The three failure layers remain distinct:

| Layer | MCP representation | Example |
|---|---|---|
| Successful durable operation | structured result with `isError: false` | `status=completed` |
| Expected tool/domain failure | structured result with `isError: true` | duplicate conflict or closed workflow |
| MCP protocol failure | protocol error response | unknown tool, malformed request, authorization filter rejection |

Tenant IDs, derived Temporal workflow IDs, run IDs, stack traces, and raw exception messages are not
included in either content channel. The application-owned operation ID is returned because it is the
caller's correlation key, not Temporal authority.

## Cancellation

MCP request cancellation stops that caller's wait. It does not cancel or terminate accepted durable
work. Applications that expose cancellation as a business operation must authenticate and authorize
it separately, and should give it an explicit idempotent contract.

## Retention and application ownership

Temporal workflow-ID reuse prevents duplicates only while the relevant execution remains retained.
If an operation key must remain idempotent longer than Temporal retention, the application owns an
atomic durable ledger keyed by authenticated tenant and operation ID. Store the completed result or
terminal conflict before retention expires. The sample uses an in-memory ledger only to make that
boundary visible; it is not a production store.

## Operational deployment

- Run the MCP web server and Temporal worker as independently scalable processes when appropriate.
- Configure both with the same Temporal namespace and the worker's task queue.
- Keep authentication keys, Temporal credentials, and durable ledger credentials out of tool
  arguments and MCP result content.
- Bound workflow runtime, activity retries, and application concurrency according to the work.
- Observe MCP authorization denials separately from Temporal workflow failures without using tenant,
  operation, workflow, or tool argument values as unbounded metric dimensions.

For MCP tools called *by* durable MEAI or MAF workers, see the [MEAI MCP client-tool guide](../MEAI/mcp-tools.md)
and [MAF MCP client-tool guide](../MAF/mcp-tools.md). For durable MCP Task protocol research, see the
[research ADR](../../architecture/MCP/durable-mcp-task-research.md).
