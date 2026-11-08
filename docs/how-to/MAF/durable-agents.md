# Durable Agents

Every agent registered with `AddDurableAgent` is a **durable agent**: each LLM call runs in a separate `RunDurableAgentStep` activity, and each tool call runs in its own `InvokeAgentTool` activity. A response's tool calls are dispatched serially in model order, so each call's result and StateBag write-back are applied before the next call starts. Tool activities use one attempt by default; retries are an explicit per-agent opt-in and apply uniformly to that agent's tools. A retry can repeat an external effect whose completion was uncertain, so use downstream idempotency and reconciliation for writes. There is no opt-in durability flag — this is the worker-hosted agent-definition path. Client-only processes use `AddTemporalAgentProxies` and `AddAgentProxy` to call an agent hosted elsewhere.

### Activities the workflow may dispatch per turn

The following activities run as needed by the durable agent loop.

| Activity name | When | What it does |
|---|---|---|
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | Every step of every turn (loop iterations) | One LLM call. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | One per tool call the LLM emits | Dispatches a single tool. Honors agent-level retry policy and per-tool timeout/approval settings |

## When to use what

- **Tools default to one activity attempt**, regardless of whether they read or write. This avoids an automatic re-execution after an ambiguous activity failure.
- **Retries are opt-in through `agent.RetryPolicy`** and apply to every tool on that agent. Only enable them when every affected tool is safe to retry, usually through an application-level idempotency key.

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

            // Read tool — one activity attempt by default.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                name: "lookup_order"));

            // Write tools — one activity attempt by default; use downstream idempotency
            // and reconcile if a failed activity leaves the effect's outcome uncertain.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    name: "apply_refund"));

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    name: "send_email"));
        });
    });
```

The library composes the chat pipeline internally — the registration above is the whole story, with no extra bootstrap or wiring on your side.

To use the agent from a regular workflow, invoke `services.GetTemporalAgentProxy("RefundAgent")`
inside a scoped activity and pass workflow-generated session and request IDs (see
[`usage.md`](./usage.md#invoking-agents-from-a-workflow)). External callers resolve the registered
proxy from their service provider.

## Fluent sugar on `DurableToolOptions`

```csharp
opts => opts.WithTimeout(TimeSpan.FromSeconds(30))

// Tool interceptor overrides (Feature L)
opts => opts.SkipInterceptor()                      // bypass IAgentToolInterceptor for this tool
opts => opts.WithInterceptorTimeout(TimeSpan.FromSeconds(10))  // per-tool interceptor activity timeout
opts => opts.RequireApproval()                      // absolute floor: always pause for human approval
                                                    // even if the interceptor returns Proceed
```

`RequireApproval()` and `PauseForApproval` are the two workflow-parked HITL triggers — the turn loop parks (no activity pinned) and retry-safe `ResolveApprovalAsync` unblocks it. This differs from the in-tool path (`TemporalAgentContext.Current.RequestApprovalAsync`), which keeps the activity running and heartbeating while waiting. See [HITL Patterns](./hitl-patterns.md) for a full comparison and the `IAgentToolInterceptor` registration pattern.

## Retry policy hierarchy

This is the canonical statement of retry inheritance for MAF agents; other pages link here rather
than restating it.

Model-step and tool activities use a bounded policy for model calls and one attempt for tools unless
an explicit agent policy opts the tools into retries. Agent-level retry policy is shared; it is not
a per-tool override.

| Activity | Default | Explicit retry configuration |
|---|---|---|
| `RunDurableAgentStep` (LLM call) | Bounded model retry policy | Agent/run/worker policy |
| `InvokeAgentTool` | One attempt | Agent/run/worker policy, applied uniformly to tools |
| `RunToolInterceptor` | One attempt | Agent/worker policy |

Three consequences people get wrong:

- **`agent.RetryPolicy` applies to tools too.** Setting it to tune a flaky model call also makes
  every registered tool retry with that policy. Do not opt in unless all tools on that agent can
  safely tolerate retries.
- **The default tool policy is not Temporal's server default.** Temporal interprets an omitted
  policy as unlimited retries; MAF supplies a one-attempt policy instead. Setting an explicit agent
  retry policy replaces that default for tool activities.

The model backstop caps backoff at 2 seconds so an interactive `RunAsync` returns its terminal
failure promptly. Tools do not retry unless the agent explicitly opts in.

Setting a per-agent `RetryPolicy` **replaces** the default policy for tool activities, so an
explicit policy with `MaximumAttempts = 0` restores Temporal's unlimited retries.

`ApprovalRequiredAIFunction` markers from MAF are carried through the workflow boundary: the
workflow requires accepted per-call approval before dispatch, and a direct tool activity call
without that approval fails non-retryably. Tools may throw
`DurableToolFailureException` only when they can guarantee failure occurred before any external
effect; its bounded, sanitized message is returned to the model. Other tool failures propagate as
activity failures and are not exposed as raw model content.

If a turn terminates after a tool activity is dispatched, later model context receives a
protocol-valid interrupted-turn projection: completed results remain paired with their calls,
uncertain dispatched outcomes are marked unknown, and undispatched calls are marked not executed.
Partial assistant prose and raw failure details are omitted. This bounded conversation context is
not an audit log; verify an unknown effect with the downstream system before retrying.

## Split-deployment behavior

In a split-deployment setup — where a client process uses `GetTemporalAgentProxy` without a full `AddDurableAgent` registration and the worker process hosts the agent via `AddDurableAgent` — the workflow starts without access to the worker's retry and timeout configuration. On the first `RunDurableAgentStep` call of each turn, the activity resolves the agent's retry policy and per-tool timeout options from the worker's registration and returns them as part of the step result.

The worker's agent-level retry policy and per-tool timeouts are honored without duplicating them on the client side.

## Sample

See [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) for an end-to-end demonstration with intentionally injected lookup failures.
