# MCP tools in durable MAF agents

`McpClientTool` derives from MEAI's `AIFunction`, so there is no Temporal-specific MCP adapter and
nothing to wrap. Hand an `McpClientTool` to `agent.AddTool(...)` and it becomes a durable tool like
any other: **the workflow dispatches each call as its own Temporal activity**, not MCP's client and
not MAF's function middleware. Retries, timeouts, and approval are the workflow's, so they work the
same way for a remote MCP tool as for a local one.

There is an [MEAI counterpart](../MEAI/mcp-tools.md) to this page — same SDK types, registered
through `AddDurableTool` instead.

---

## Connect and register

The MCP client belongs to the worker process, not to a session. Create it during startup, before
the worker starts, and register the tools you want by exact name:

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

`StringComparer.Ordinal` is deliberate. Selecting a destructive tool by a case-insensitive or
prefix match is how you end up registering something the server renamed underneath you.

---

## Choose your catalog: discovery or pinned definitions

Whether you call `ListToolsAsync()` decides who controls the schema the model sees — you, or
whatever the server returns at worker start.

| | Live discovery | Pinned definitions |
|---|---|---|
| Source of the catalog | `await mcp.ListToolsAsync()` | `Tool` definitions checked into source control |
| Schema the model sees | Whatever the server returns today | Whatever you reviewed |
| A tool added server-side | Appears automatically | Ignored until you add it |
| A schema changed server-side | Silently changes the model's surface | Ignored; your pinned schema still applies |
| Suits | Trusted development servers | Production, and anything with side effects |

Discovery is one line:

```csharp
agent.AddTools(discovered);   // every returned tool, each with the default policy
```

Pinning constructs the wrappers yourself from reviewed definitions, so the server is never asked
what exists:

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

`AddTools` throws `ArgumentException` on a name already registered on that agent, so merging two
catalogs fails loudly rather than silently shadowing a tool.

---

## The default policy retries

This is the part worth reading before `AddTools(discovered)`.

A tool registered with no policy of its own falls through `agent.RetryPolicy` and
`opts.DefaultRetryPolicy` to the library's bounded backstop: **five attempts**, with a 30-second
maximum backoff. The backstop is not the worker default — it is what applies when no worker,
agent, or per-tool policy was set at all, and the library substitutes
`new RetryPolicy { MaximumAttempts = 5 }` rather than letting Temporal's server default
(`MaximumAttempts = 0`, unlimited) apply. Setting `opts.DefaultRetryPolicy` replaces it; see
[Durable Agents](./durable-agents.md#retry-policy-hierarchy).

Five attempts is right for a read. It is wrong for anything that has an effect: a `delete_inventory`
that times out after doing the delete will be called again. Write-style MCP tools need `NoRetry()`,
and `AddTools(discovered)` gives them the opposite:

```csharp
agent.AddTools(reads);                                  // lookups — retry is a feature
agent.AddTool(byName["delete_inventory"],
    policy => policy.NoRetry().RequireApproval());      // effects — register individually
```

There is no way to tell from an MCP catalog which tools have effects. The server does not say, and
the library cannot guess, so this is a judgement you have to make per tool.

---

## Approval

`RequireApproval()` pauses the turn before the tool is dispatched. It is an **absolute
configuration-time floor**: it holds even when no `IAgentToolInterceptor` is registered, and even if
an interceptor returns `Proceed`.

The pause is not self-resolving. Something outside the workflow has to answer it:

```csharp
var pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
if (pending is not null)
{
    await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,   // required — ties the answer to the request
            Approved = true,
        },
        cancellationToken);
}
```

`GetPendingApprovalAsync` is a workflow query — it never blocks and adds no history, so poll it
freely. If nothing ever resolves the request, the turn waits until `DefaultApprovalTimeout`
(7 days) expires.

Approval mechanics — what the reviewer is shown, what the model sees when a call is denied, how
this interacts with activity timeouts, and reusable session grants — belong to
[hitl-patterns.md](./hitl-patterns.md). Nothing about them is MCP-specific.

---

## Client lifetime

**Keep the `McpClient` out of session state and workflow state.** It holds a live transport: it is
not serializable, and a workflow that captured one would not survive replay on another worker. The
session carries conversation history and the `StateBag`, both of which cross machines; an MCP
connection cannot.

The shape that works is the one above — `await using` in worker startup, so the client outlives
every session on that worker and is disposed after the host stops. Each tool activity uses the
live client on whichever worker picks the activity up.

Because the activity runs long after the model asked for the call, **reauthorize immediately before
the effect**, inside the tool, against current authoritative state. Approval granted at request time
is not authorization at execution time.

---

## See also

- [`samples/MAF/McpTools`](../../../samples/MAF/McpTools/) — runnable both ways; `--dynamic`
  switches from pinned definitions to live discovery. No API key or external MCP server needed.
- [`docs/how-to/MEAI/mcp-tools.md`](../MEAI/mcp-tools.md) — the MEAI equivalent
- [hitl-patterns.md](./hitl-patterns.md) — approval mechanics
- [durable-agents.md](./durable-agents.md) — per-tool activities and retry policy in general
- [security.md](../../security.md) — the application boundary this page defers to
