# Durable Agents

Every agent registered with `AddDurableAgent` is a **durable agent**: each LLM call runs in a separate `RunDurableAgentStep` activity, and each tool call runs in its own `InvokeAgentTool` activity, all dispatched in parallel via `Workflow.WhenAllAsync`. Every tool shares that one activity type name; what differs per tool is the activity options — retry policy and timeouts are resolved by tool name at dispatch. There is no opt-in flag — it is the only worker-hosted agent-definition path. Client-only processes use `AddTemporalAgentProxies` and `AddAgentProxy` to call an agent hosted elsewhere. This makes per-tool retry granularity explicit and prevents the foot-gun where write-style tools could re-fire on a transient activity retry.

### Activities the workflow may dispatch per turn

The following activities run as needed by the durable agent loop.

| Activity name | When | What it does |
|---|---|---|
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | Every step of every turn (loop iterations) | One LLM call. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | One per tool call the LLM emits | Dispatches a single tool. Honors per-tool `DurableToolOptions` |

## When to use what

- **Read tools** (lookup, query, fetch): leave the per-tool retry policy unset; they fall through to the per-agent, then worker, then the library's bounded default of five attempts.
- **Write tools** (send_email, apply_refund, write_record): always pass `opts => opts.NoRetry()` (or set a small `MaximumAttempts`) so a worker crash cannot re-issue the side effect.

## Canonical example

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();

builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.Instructions = "You are a refund specialist...";
            agent.MaxToolCallsPerTurn = 10;  // caps the per-turn LLM↔tool loop; default 20 — see usage.md

            // Read tool — retries on transient failure, bounded at five attempts by default.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                name: "lookup_order"));

            // Write tools — never retry, never re-fire on activity-level retry.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    name: "apply_refund"),
                opts => opts.NoRetry());

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    name: "send_email"),
                opts => opts.NoRetry());
        });
    });
```

The library composes the chat pipeline internally — the registration above is the whole story, with no extra bootstrap or wiring on your side.

To use the agent, call `WorkflowAgents.GetTemporalAgent("RefundAgent")` inside a workflow (see [`usage.md`](./usage.md)), or `services.GetTemporalAgentProxy("RefundAgent")` from external code.

## Fluent sugar on `DurableToolOptions`

```csharp
opts => opts.NoRetry()                              // RetryPolicy { MaximumAttempts = 1 }
opts => opts.WithMaxAttempts(3)
opts => opts.WithTimeout(TimeSpan.FromSeconds(30))

// Tool interceptor overrides (Feature L)
opts => opts.SkipInterceptor()                      // bypass IAgentToolInterceptor for this tool
opts => opts.WithInterceptorTimeout(TimeSpan.FromSeconds(10))  // per-tool interceptor activity timeout
opts => opts.RequireApproval()                      // absolute floor: always pause for human approval
                                                    // even if the interceptor returns Proceed
opts => opts.ScopeAware()                           // opt in to expiring-grant auto-approval —
                                                    // requires agent.UseApprovalScopes(); see hitl-patterns.md
```

`RequireApproval()` and `PauseForApproval` are the two workflow-parked HITL triggers — the turn loop parks (no activity pinned) and retry-safe `ResolveApprovalAsync` unblocks it. This differs from the in-tool path (`TemporalAgentContext.Current.RequestApprovalAsync`), which keeps the activity running and heartbeating while waiting. See [HITL Patterns](./hitl-patterns.md) for a full comparison and the `IAgentToolInterceptor` registration pattern.

## Retry policy hierarchy

This is the canonical statement of retry inheritance for MAF agents; other pages link here rather
than restating it.

Three activity types resolve a retry policy, and they share most of the chain. First match wins:

| Rung | Tool call (`InvokeAgentTool`) | LLM call (`RunDurableAgentStep`) | Interceptor (`RunToolInterceptor`) |
|---|---|---|---|
| 1 | `DurableToolOptions.RetryPolicy` — the `configure` callback on `AddTool` | `OneTimeAgentRun.RetryPolicy` — scheduled and one-time runs only | *(no per-tool rung)* |
| 2 | `agent.RetryPolicy` | `agent.RetryPolicy` | `agent.RetryPolicy` |
| 3 | `opts.DefaultRetryPolicy` | `opts.DefaultRetryPolicy` | `opts.DefaultRetryPolicy` |
| 4 | bounded backstop — 5 attempts, 30s max interval | bounded backstop — 5 attempts, 2s max interval | bounded backstop — 5 attempts, 30s max interval |

Two consequences people get wrong:

- **`agent.RetryPolicy` is not LLM-only.** Rung 2 is shared. An agent-level policy set to tune model
  retries also becomes the default for every tool on that agent and for the interceptor activity.
  Only `DurableToolOptions.RetryPolicy` is tool-specific.
- **The backstop is not a worker default you can read back.** Rung 4 exists precisely *because*
  rungs 1–3 are all unset. Leaving `opts.DefaultRetryPolicy` null does not mean "no retry policy" —
  it means the library substitutes `MaximumAttempts = 5` in place of Temporal's server default of
  `MaximumAttempts = 0`, which is **unlimited**. An unbounded retry on a permanently failing tool or
  model call would otherwise never surface as a failure.

The model backstop caps backoff at 2 seconds rather than 30 because an interactive `RunAsync` should
return its terminal failure promptly; tool work gets the longer window so a transient dependency has
a meaningful chance to recover.

Setting `DefaultRetryPolicy` or a per-agent `RetryPolicy` **replaces** the backstop rather than
layering on it, so an explicit policy with `MaximumAttempts = 0` does restore unlimited retries.

## Split-deployment behavior

In a split-deployment setup — where a client process uses `GetTemporalAgentProxy` without a full `AddDurableAgent` registration and the worker process hosts the agent via `AddDurableAgent` — the workflow starts without access to the worker's per-tool configuration. On the first `RunDurableAgentStep` call of each turn, the activity detects this state and resolves per-tool retry and timeout options from the worker's `DurableAgentRegistration` in memory, returning them as part of the step result. The workflow then uses these resolved options for all subsequent tool dispatches in that turn.

This means per-tool options set via `agent.AddTool(tool, opts => opts.NoRetry())` are honored correctly in split deployments — they do not need to be (and cannot be) configured on the client side.

## Sample

See [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) for an end-to-end demonstration with intentionally injected lookup failures.
