# Durable Agents

Every agent registered with `AddDurableAgent` is a **durable agent**: each LLM call runs in a separate `RunDurableAgentStep` activity, and each tool call runs in its own `InvokeAgentTool` activity. A response's tool calls are dispatched serially in model order, so each call's result and StateBag write-back are applied before the next call starts. Tool activities use one attempt by default; only the per-agent retry policy opts all of that agent's tools into retries. Worker retry defaults also govern context preparation, not memory-scope initialization, tools, or interceptors. A retry can repeat an external effect whose completion was uncertain, so use downstream idempotency and reconciliation for writes. There is no opt-in durability flag — this is the worker-hosted agent-definition path. Client-only processes use `AddTemporalAgentProxies` and `AddAgentProxy` to call an agent hosted elsewhere.

### Activities the workflow may dispatch per turn

The following activities run as needed by the durable agent loop.

| Activity name | When | What it does |
|---|---|---|
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | Every step of every turn (loop iterations) | One LLM call. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | One per tool call the LLM emits | Dispatches a single tool. Honors agent-level retry policy and per-tool timeout/approval settings |

## When to use what

- **Tools default to one activity attempt**, regardless of whether they read or write. This avoids an automatic re-execution after an ambiguous activity failure.
- **Retries are opt-in through `agent.RetryPolicy`** and apply to every tool on that agent. `opts.DefaultRetryPolicy` controls model-step retries only. Enable agent-level retries only when every affected tool is safe to retry, usually through an application-level idempotency key.

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

Model-step and context-preparation activities use the bounded model policy. Memory-scope
initialization uses a dedicated fixed policy. Tool activities and interceptor activities use one
attempt unless an explicit agent policy opts them into retries. The worker-level retry policy
applies to model and context-preparation activities only; tool retries are not inherited from the
worker.

| Activity | Default | Explicit retry configuration |
|---|---|---|
| `RunDurableAgentStep` (LLM call) | Five attempts, maximum backoff 2 seconds | Agent policy, then worker default |
| Context preparation | Same timeout, heartbeat, and retry policy as model steps | Agent policy, then worker default |
| Memory-scope initialization | 30-second start-to-close, two attempts, 1-second initial interval, no heartbeat | Fixed policy |
| `InvokeAgentTool` | One attempt | Per-agent policy, applied uniformly to tools |
| `RunToolInterceptor` | One attempt | Per-agent policy |

Important consequences:

- **`agent.RetryPolicy` applies to tools too.** Setting it to tune a flaky model call also makes
  every registered tool retry with that policy. Do not opt in unless all tools on that agent can
  safely tolerate retries.
- **The default tool policy is not Temporal's server default.** Temporal interprets an omitted
  policy as unlimited retries; MAF supplies a one-attempt policy instead. Setting an explicit agent
  retry policy replaces that default for tool activities.
- **Worker defaults do not opt tools into retries.** `opts.DefaultRetryPolicy` affects model-step
  and context preparation, never memory-scope initialization, tools, or interceptors. There is no run-level
  retry option on `TemporalAgentRunOptions`.
- **Per-tool retries are not independent.** `WithMaxAttempts(2)` is rejected. `NoRetry()` is accepted
  only when the agent policy uses one attempt; it cannot override an agent policy with multiple
  or unlimited attempts.

The model backstop caps backoff at 2 seconds so an interactive `RunAsync` returns its terminal
failure promptly. Tools do not retry unless the agent policy explicitly opts in.

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

In a split deployment, register the Temporal client and client-only proxies with
`AddTemporalClient` and `AddTemporalAgentProxies`, then host the agent on the worker. The proxy
declares only the agent name and task queue. The worker's workflow instance factory supplies the
registered agent's execution settings before workflow initialization; there is no settings lookup
activity or first-step handshake.

If a proxy names an agent that is not registered on that worker, synchronous calls and
fire-and-forget admission fail with an `AgentNotRegisteredException` application failure rather
than repeatedly retrying workflow startup. Fire-and-forget waits only for the queueing update's
outcome, not for the model or tools to finish.

The worker owns session TTL, model and context-preparation activity timeouts, heartbeat and approval
timeouts, retry policy, history bounds, and tool/interceptor policy. The same worker model activity
options govern both model steps and context preparation, including heartbeats and retries.
Memory-scope initialization keeps its dedicated fixed policy. Those settings are carried through
Continue-as-New rather than refreshed each turn. Worker
configuration must remain compatible with in-flight workflow histories during replay.

`DefaultRetryPolicy` applies to model steps and context preparation, never enabling tool or
interceptor retries. Memory-scope initialization uses its fixed policy. Tools and interceptors
remain one attempt unless an explicit per-agent
`RetryPolicy` opts them into retries; only use that setting when all affected operations are safe
to repeat.

## Sample

See [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) for an end-to-end demonstration with intentionally injected lookup failures.
