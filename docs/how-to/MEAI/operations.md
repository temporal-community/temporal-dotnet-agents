# Operating managed chat on separate queues

Managed durable chats can route model steps and individual tool invocations to separate Temporal
task queues. This is an opt-in deployment/capacity choice, not a priority scheduler or tenant
authorization boundary. Null routing options preserve the existing workflow-queue behavior.

| Work | Routing source | Default |
|---|---|---|
| Session workflow | `AddDurableAIClient(taskQueue)` / co-hosted worker queue | Required session queue |
| Managed model step (`GetChatStep`) | `DurableExecutionOptions.ModelTaskQueue` in the session-starting process | Workflow queue |
| Managed tool invocation (`InvokeFunction`) | `DurableChatToolOptions.TaskQueue` for that tool | Workflow queue, **not** the model queue |
| Toolset resolution, interceptors, approvals, history reduction/summarization | Existing workflow/policy routing | Unchanged; these options do not move them |

These settings also apply to the managed loop in `DurableToolWorkflowBase`. They do not change
MAF agent routing, direct `DurableChatClient` adapters, or `AIFunction.AsDurable()` routing.
No new queue is created or polled just because its name appears in configuration.

## Register workers

This co-hosted example runs three worker pools in one process. It is also a compile-checked
registration example: supply `services`, your `IChatClient` as `innerClient`, and a read-only
`AIFunction` as `lookupTool`. The model and tool workers register activities without registering
the default session workflow:

```csharp
services.AddTemporalClient("localhost:7233", "default");
services.AddChatClient(innerClient);

services.AddHostedTemporalWorker("chat-sessions")
    .AddDurableAI(options => options.ModelTaskQueue = "chat-model")
    .AddDurableTool(lookupTool, options =>
    {
        options.TaskQueue = "chat-tools";
        options.Idempotent().WithMaxAttempts(2);
    });

services.AddHostedTemporalWorker("chat-model")
    .ConfigureOptions(options => options.MaxConcurrentActivities = 8)
    .AddDurableAIWorker(options => options.RegisterDefaultWorkflow = false);

services.AddHostedTemporalWorker("chat-tools")
    .ConfigureOptions(options => options.MaxConcurrentActivities = 16)
    .AddDurableAIWorker(options => options.RegisterDefaultWorkflow = false);
```

<!-- Compiled in tests/docs/DocSnippets/Snippets/MEAI/Operations.cs. -->

All three workers share DI in this example. For separate processes, use separate service
providers and register the appropriate providers, tools and dependencies in each. Public
`AddDurableAIWorker` registers the package activity set, not just one activity type; the workflow's
recorded routing decides where the model and tool invocations actually execute. The current
worker registration also validates `IChatClient` and toolset services locally, even in an
activity-only process. Register the configured chat-client profiles, reducers and instrumentation
where used; preserve the same converter/codec at every payload boundary.

The session worker must know the selected tool declarations and durable policy so it can resolve
the baseline once. A tool worker must know the matching implementations, toolset IDs, member
registration order and schemas so activation identities match. Register the same named toolsets
consistently across those processes. A queue name alone does not move tool implementations or
grant execution authority.

Only mark a tool idempotent when its downstream operation is safe to retry or enforces the
library-provided logical operation key. Separating its worker does not make an effect exactly-once.

## Freeze routing at session creation

In split hosting, the process that starts sessions sets the model queue:

```csharp
clientServices.AddTemporalClient("localhost:7233", "default");
clientServices.AddDurableAIClient("chat-sessions", options =>
{
    options.ModelTaskQueue = "chat-model";
});
```

<!-- Compiled in tests/docs/DocSnippets/Snippets/MEAI/Operations.cs. -->

The factory serializes that model route in `DurableChatWorkflowInput`. Worker-only model queue
configuration cannot override an existing session's input. Per-tool queues are recorded in the
worker-owned toolset manifest when the session initializes; advanced caller-owned declarations
instead carry them in the input's `ToolActivityOptions`. Both routes survive continue-as-new.
The history-recorded manifest fingerprint includes the tool's queue.

Changing configuration affects newly initialized sessions, not already recorded routing. Keep
old queue pollers available while those sessions drain, or explicitly end the session lineage
and start another under an application-controlled migration. Do not expect a deployment to
silently reroute existing work. Queue overrides must be null or non-whitespace strings; names
are used exactly as configured, without trimming or normalization.

## Capacity, priority and throttling

Separate queues can reserve capacity for interactive versus background work, isolate slow tools,
and scale worker pools independently. **Queues do not themselves guarantee cross-queue priority,
preemption or fairness.** A worker on one queue does not choose tasks from another. Allocate
dedicated replicas/resources to higher-service-level queues and measure their latency rather
than assuming a queue name establishes priority. Model and tool work for one turn still obey the
session's one-active-turn and tool-dispatch rules.

Use Temporal's existing worker options rather than a library priority/rate-limit API:

| Control | What it bounds |
|---|---|
| `MaxConcurrentActivities` | Running activities on one worker; replicas multiply capacity. |
| `MaxActivitiesPerSecond` | Activity dispatch rate for one worker; not a global provider quota. |
| `MaxTaskQueueActivitiesPerSecond` | Server-side activity dispatch rate for a task queue. Configure consistently across its pollers; it is not a token budget or cross-queue fairness policy. |
| Provider quotas / application controls | Provider request/token budgets and downstream tool limits; aggregate across queues, workers and applications that share credentials. |

The SDK can execute eligible activities eagerly on the originating workflow worker; the package
preserves that default. If relying on `MaxTaskQueueActivitiesPerSecond`, set the existing
`TemporalWorkerOptions.DisableEagerActivityExecution = true` on the workflow workers that schedule
that work, and configure the queue rate consistently on its activity pollers. Do not assume a
server queue-rate limit also throttles eager execution. Start with modest concurrency, monitor
queue age and provider throttling, and add capacity intentionally. A replica count or concurrency
ceiling is not requests-per-second, and neither is tokens-per-minute. Retries consume additional
attempts/capacity and can amplify throttling.

## Missing or wrong queues

An unpolled queue can leave a turn waiting indefinitely for an activity to **start**. The managed
`ActivityTimeout` / tool `StartToCloseTimeout` starts only after a worker picks up the activity;
`HeartbeatTimeout` also does not bound queue wait. These two routing options add no new
schedule-to-start or end-to-end timeout policy. A timeout/cancellation of the caller's `SendAsync`
RPC wait is not a reliable cancellation of the admitted turn.

Inspect the scheduled activity's task queue, current pollers, pending tasks and
schedule-to-start latency in Temporal. Restore a compatible poller or use `CancelTurnAsync` with
the known logical request ID, then inspect `GetTerminalTurnOutcomeAsync`. Treat an already started
tool's external outcome as potentially unknown after cancellation/timeout. Do not resubmit with a
new request ID just to escape a missing worker. `DurableActivityRoutingIntegrationTests` covers
unpolled model and tool queues: neither times out just because start-to-close elapsed, and explicit
turn cancellation completes and replays.

## Search and telemetry safety

Use a small, approved set of operational queue names and opaque routing metadata. Do not create a
queue per session or embed prompts, customer IDs or secrets in queue names. Task queue names,
workflow IDs and search attributes are observable operational metadata, not encrypted content.
Queues are not an authentication/tenant-isolation substitute.

MEAI typed search attributes remain opt-in with `EnableSearchAttributes` and require server-side
registration (`SessionCreatedAt` and `TurnCount`). Derived workflows may use approved opaque
attributes for operational lookup; do not put sensitive identifiers or prompts in plain-text
search attributes. There is no new search-attribute or priority API in this routing feature.

Package-owned metrics do not add tenant, session, request or queue IDs as labels; the metric-label
regression exercises real model/tool activity paths to keep those dimensions bounded. Traces can
carry documented correlation fields under the existing capture policy. Temporal SDK/service
metrics and application/provider exporters have their own dimensions and privacy configuration;
review them separately. See [observability](observability.md) and
[payload codecs](payload-codecs.md).
