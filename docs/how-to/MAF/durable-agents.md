# Durable Agents

This guide covers the worker-side `AddDurableAgent` / `DurableAgentBuilder` path. For the standard
MAF `Agent(..., ChatClientAgentOptions, ...)` registration and client-only proxies, see the
[registration guide](./usage.md#registration).

Each model step runs in a `RunDurableAgentStep` activity. Each tool call the workflow dispatches
runs in an `InvokeAgentTool` activity. Calls from one model response run serially in model order,
and each result and StateBag update is applied before the next tool starts. There is no separate
per-tool activity switch.

### Activities in an agent turn

The workflow prepares each turn, runs model steps and any dispatched tools, then records provider
completion hooks. Before the first turn it initializes chat-history memory when configured; without
that feature, the initialization activity is a no-op.

| Activity name | When | What it does |
|---|---|---|
| `TemporalCommunity.Extensions.Agents.InitializeChatHistoryMemory` | Before the first turn's preparation | Establishes or validates the chat-history memory scope. |
| `TemporalCommunity.Extensions.Agents.PrepareAgentTurn` | Once per turn, before the first model step | Prepares provider context and the tool set for the turn. |
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | Each model step | Makes one model call and returns a final response or tool-call batch. |
| `TemporalCommunity.Extensions.Agents.RunToolInterceptor` | For each eligible tool call when an interceptor is configured | Evaluates the call; eligible interceptor activities run concurrently and decisions are collected before tool dispatch. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | For each tool call actually dispatched, serially in model order | Executes one tool after any approval wait and returns its result and StateBag update. |
| `TemporalCommunity.Extensions.Agents.CompleteAgentTurn` | At each terminal turn outcome | Runs provider completion hooks. |

A tool call blocked, skipped, or denied by approval receives a synthetic result and does not run an
`InvokeAgentTool` activity. Approval waits are handled by the workflow before tool activities start.

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

Factory-registered tools are created from the activity-scoped service provider when the worker
resolves them for preparation, model-step, or tool-call activities. A factory can run again on an
activity retry, so keep function construction side-effect-free and store per-session state in the
workflow-owned StateBag rather than process fields. External effects belong in the tool function.

To use the agent from a regular workflow, invoke `services.GetTemporalAgentProxy("RefundAgent")`
inside a scoped activity and pass workflow-generated session and request IDs (see
[`usage.md`](./usage.md#invoking-agents-from-a-workflow)). External callers resolve the registered
proxy from their service provider.

The application still registers its chat client, Temporal client, and worker. When search attributes
are enabled (the default), register their definitions before starting the worker; see the
[quickstart prerequisites](./quickstart.md#before-you-start).

## Per-tool options

`agent.AddTool(...)` accepts an optional `DurableToolOptions` callback:

- `WithTimeout(...)` sets that tool activity's start-to-close timeout.
- `SkipInterceptor()` bypasses the configured interceptor for that tool.
- `WithInterceptorTimeout(...)` sets that tool's interceptor-activity timeout.
- `RequireApproval()` requires human approval before dispatch, even if the interceptor returns
  `Proceed`.

`RequireApproval()` and `PauseForApproval` park the workflow until `ResolveApprovalAsync` resolves
the decision. By contrast, `TemporalAgentContext.Current.RequestApprovalAsync` waits inside the
tool activity, which continues heartbeating. See [HITL Patterns](./hitl-patterns.md) for the
comparison and interceptor setup. For an `AddTool` options-callback example, see
[tool activity ordering and retry policy](./usage.md#tool-activity-ordering-and-retry-policy).

## Configure retries safely

The worker-level `DefaultRetryPolicy` applies to model-step and context-preparation activities.
Memory initialization has a fixed timeout and retry policy; provider completion has a fixed retry
count but uses the worker/agent activity timeout (or 10 seconds during cancellation). Tool and
interceptor activities use one attempt unless the agent's `RetryPolicy` opts both into retries.

| Activity | Default | Explicit retry configuration |
|---|---|---|
| `RunDurableAgentStep` (model call) | Up to five attempts; maximum retry interval 2 seconds | Agent `RetryPolicy`, otherwise worker `DefaultRetryPolicy` |
| `PrepareAgentTurn` (context preparation) | Same timeout, heartbeat, and retry policy as model steps | Agent `RetryPolicy`, otherwise worker `DefaultRetryPolicy` |
| `InitializeChatHistoryMemory` | 30-second start-to-close, two attempts, 1-second initial interval, no heartbeat | Fixed policy |
| `CompleteAgentTurn` (provider completion) | Up to three attempts; one during cancellation | Retry count is fixed; activity timeout follows worker/agent settings (10 seconds during cancellation) |
| `InvokeAgentTool` | One attempt | Agent policy, applied to all tools |
| `RunToolInterceptor` | One attempt | Per-agent policy |

Important consequences:

- **`agent.RetryPolicy` applies to tools too.** Setting it to tune a flaky model call also makes
  every registered tool and interceptor retry with that policy. Do not opt in unless all affected
  effects can safely tolerate retries.
- **The default tool policy is not Temporal's server default.** Temporal interprets an omitted
  policy as unlimited retries; this integration supplies a one-attempt policy instead. Setting an
  explicit agent retry policy replaces that default for tool and interceptor activities.
- **Worker defaults do not opt tools into retries.** `opts.DefaultRetryPolicy` affects model-step
  and context preparation, not memory initialization, provider completion, tools, or interceptors.
  There is no per-run retry option on `TemporalAgentRunOptions`.
- **Per-tool retries are not independent.** `WithMaxAttempts(2)` is rejected. `NoRetry()` is accepted
  only when the agent policy uses one attempt; it cannot override an agent policy with multiple
  or unlimited attempts.

The default model policy caps retry intervals at 2 seconds, but total latency still depends on
activity timeouts and provider latency.

Setting a per-agent `RetryPolicy` **replaces** the default policy for tool activities, so an
explicit policy with `MaximumAttempts = 0` restores Temporal's unlimited retries.

`ApprovalRequiredAIFunction` markers from MAF are carried through the workflow boundary: the
workflow requires accepted per-call approval before dispatch, and a direct tool activity call
without that approval fails non-retryably. A tool may throw `DurableToolFailureException` only
when it can guarantee failure occurred before any external effect. Its caller-supplied
`ModelVisibleMessage` is returned to the model and must be safe to expose; it is limited to 1,024
UTF-8 bytes. Other tool failures propagate as activity failures and are not exposed as raw model
content.

Before dispatching a tool batch, the workflow reserves space for an interruption record, capped at
the smaller of 64 KiB and the configured transport-payload limit. If the record cannot fit, no
tools in that batch are dispatched. If a turn terminates after dispatch, later model context
receives a protocol-valid interruption record: dispatched calls without a known result are marked
unknown, and undispatched calls are marked not executed. A compact record may omit completed
results while preserving call/result pairing. Partial assistant prose and raw failure details are
omitted. This bounded conversation context is not an audit log; verify an unknown effect with the
downstream system before retrying.

## Split-deployment behavior

In a split deployment, the caller registers a Temporal client and the proxy names/task queue with
`AddTemporalClient` and `AddTemporalAgentProxies`; the worker registers the agent definition and
owns its execution settings. The worker applies its registration before workflow initialization.

If a proxy names an agent that is not registered on that worker, synchronous calls and
fire-and-forget admission fail with a non-retryable `AgentNotRegisteredException` application
failure. Fire-and-forget waits for the queueing update's outcome, not for the model or tools to
finish.

The worker resolves session TTL, default activity/heartbeat/approval timeouts, retry policy, history
bounds, and tool/interceptor policy. Agent- and per-tool settings override applicable defaults.
Model steps and context preparation share the resolved activity and heartbeat timeouts and model
retry policy; an agent retry policy also governs its tools and interceptors. Memory initialization
uses a fixed timeout/retry policy, while provider completion uses a fixed retry count and the
resolved activity timeout. Resolved settings are carried through Continue-as-New rather than
re-read from worker options.

## Sample

See [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) for an end-to-end demonstration with intentionally injected lookup failures.
