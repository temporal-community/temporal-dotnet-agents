# MCP tools in durable MAF agents

`McpClientTool` derives from MEAI's `AIFunction`, so no Temporal-specific MCP adapter is needed.
Register it with `agent.AddTool(...)`: **the workflow dispatches each call as its own Temporal
activity**, rather than invoking it inline in the model step. Temporal activity timeouts and retry
settings apply to the call; configured approval is enforced before dispatch.

There is an [MEAI counterpart](../MEAI/mcp-tools.md) to this page — same SDK types, registered
through `AddDurableTool` instead.

---

## Connect and register

The MCP client belongs to the worker process, not to a session. Create it in the process-level
composition root before the worker starts, and keep it alive until after the host stops. Register
the tools you want by exact name:

```csharp
// Keep this process-level scope alive until after the worker host stops.
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

Use exact ordinal name matching for an allowlist; do not broaden selection with case folding or
prefix matching.

---

## Choose your catalog: discovery or pinned definitions

`ListToolsAsync()` reads the server's catalog when called; it does not update tools already
registered on a running worker. Whether you expose the discovered catalog or checked-in
definitions controls which schema the model sees.

| | Live discovery | Pinned definitions |
|---|---|---|
| Source of the catalog | `ListToolsAsync()` at startup | `Tool` definitions checked into source control |
| Schema the model sees | What the server returned at discovery | What you reviewed |
| New server tool | Included at next startup only if you register all discovered tools | Requires a reviewed definition |
| Server schema changes | Reflected after discovery; may change model calls | Model-facing schema stays pinned; the server may reject calls |
| Use when | Startup should follow a trusted server catalog | Model-facing tools need review and version control |

Discovery is one line:

```csharp
agent.AddTools(discovered);   // every returned tool, each with the default policy
```

Pinning constructs wrappers from checked-in definitions without calling `ListToolsAsync()`:

```csharp
IReadOnlyList<Tool> reviewed = LoadCheckedInDefinitions();

foreach (var definition in reviewed)
{
    agent.AddTool(new McpClientTool(mcp, definition));
}
```

**What pinning buys you, and what it does not.** A pinned definition controls the schema shown to
the model. It does not authenticate the server, and it does not prove the running implementation
still matches the definition you reviewed — the call still goes to whatever is on the other end of
the transport. Authenticate the transport and authorize the effect in the authoritative service.
See [the tool and schema boundary](../../security.md#tool-and-schema-boundary).

`AddTools` throws `ArgumentException` when a tool name is already registered on that agent
(comparison is case-insensitive), so merging catalogs fails rather than silently shadowing a tool.

---

## Tool retries are opt-in

This is the part worth reading before `AddTools(discovered)`.

Every tool and tool-interceptor activity gets **one attempt by default**, including tools added
through `AddTools`. `agent.RetryPolicy` overrides the worker default for that agent's model and
context-preparation activities and applies the same policy to every tool and interceptor on the
agent. The worker-level `opts.DefaultRetryPolicy` applies to model and context-preparation
activities only; memory-scope initialization uses a separate fixed policy.

`NoRetry()` can make the one-attempt setting explicit, but it cannot override an agent-level retry
policy; conflicting settings are rejected. Per-tool retry counts above one are not supported. Only
set agent-wide retries when every affected activity is safe to repeat, usually because the
authoritative service enforces an idempotency key.

When an MCP catalog mixes reads and writes, keep the default one-attempt policy or split the tools
across agents with appropriate policies:

```csharp
agents.Agent(
    "InventoryAgent",
    new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions
        {
            Tools = [.. reads, byName["delete_inventory"]],
        },
    });
```

The MAF-options registration path applies retry policy at the agent level, not per tool. Keep the
one-attempt default for a mixed catalog, or split retry-safe tools and effectful tools across
different agents. MCP tool definitions may include `readOnlyHint`, `destructiveHint`, and
`idempotentHint`, but these are advisory metadata, not trusted policy inputs; this integration does
not use them to derive retry or approval policy. Configure those policies explicitly.

---

## Approval

`RequireApproval()` pauses the turn before the tool is dispatched. It is an **absolute
configuration-time floor**: it holds even when no `IAgentToolInterceptor` is registered, and even if
an interceptor returns `Proceed`.

The workflow waits for an external decision or the configured approval timeout; expiry rejects the
tool call. `DefaultApprovalTimeout` defaults to 7 days and can be overridden at worker or agent
registration. The example assumes `client` is an `ITemporalAgentClient` and `sessionId` is the
current `TemporalAgentSessionId`:

```csharp
var pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
if (pending is not null)
{
    await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,   // required — ties the answer to the request
            CallId = pending.CallId,
            FunctionName = pending.FunctionName,
            Approved = true,
        },
        cancellationToken);
}
```

`GetPendingApprovalAsync` returns the current pending request or `null`; it does not wait for a
future decision or add an event to workflow history. Poll from your application as needed, using an
appropriate cadence.

Approval mechanics — what the reviewer is shown, what the model sees when a call is denied, how
this interacts with activity timeouts — belong to
[hitl-patterns.md](./hitl-patterns.md). Nothing about them is MCP-specific.

---

## Client lifetime

**Keep the `McpClient` out of session and workflow data.** It is a live process-local transport.
The workflow records the tool name and arguments; the activity invokes the MCP client registered
on its worker.

The `await using` scope must outlive the worker host, so the client is disposed after the host
stops. Each tool activity uses the client registered in the worker process that executes it; every
worker polling the agent's task queue must have the agent and its MCP client configured.

Because the activity may run after the model requested the call or approval was granted, enforce
authorization on the MCP server or authoritative service when the effect occurs. Workflow approval
is not authorization against current state.

---

## See also

- [`samples/MAF/McpTools`](../../../samples/MAF/McpTools/) — runnable both ways; `--dynamic`
  switches from pinned definitions to live discovery. No API key or external MCP server needed.
- [`docs/how-to/MEAI/mcp-tools.md`](../MEAI/mcp-tools.md) — the MEAI equivalent
- [hitl-patterns.md](./hitl-patterns.md) — approval mechanics
- [durable-agents.md](./durable-agents.md) — per-tool activities and retry policy in general
- [security.md](../../security.md) — the application boundary this page defers to
