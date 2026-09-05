# Observability

How to instrument, trace, and query TemporalAgents workloads — from OpenTelemetry span setup to search attribute queries in the Temporal UI.

---

## Table of Contents

1. [Overview](#overview)
2. [Setup](#setup)
3. [Span Reference](#span-reference)
4. [Attribute Reference](#attribute-reference)
5. [Full Span Hierarchy](#full-span-hierarchy)
6. [Search Attributes](#search-attributes)
7. [Correlating Across Continue-as-New](#correlating-across-continue-as-new)
8. [Operational Patterns](#operational-patterns)

---

## Overview

TemporalAgents participates in up to three tracing layers:

1. **Agent spans** — emitted by `TemporalAgentTelemetry.ActivitySource` (`"TemporalCommunity.Extensions.Agents"`) to capture agent-semantic events like "agent turn" and "client send"
2. **Temporal SDK spans** — emitted by the `TracingInterceptor` from `Temporalio.Extensions.OpenTelemetry` to capture protocol-level events like `StartWorkflow` and `RunActivity`
3. **Canonical GenAI spans (optional)** — emitted by MAF `OpenTelemetryAgent` or MEAI
   `OpenTelemetryChatClient` under the source name supplied when that middleware is configured

These layers compose into one trace from the external caller through the workflow and activity to
the model invocation.

By default, `AgentWorkflow` upserts **search attributes** on each workflow run, enabling operational queries in the Temporal Web UI and via the `ListWorkflowsAsync` API. Set `EnableSearchAttributes = false` to opt out.

---

## Setup

Install the required packages:

```bash
dotnet add package Temporalio.Extensions.OpenTelemetry
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol  # or your preferred exporter
```

Register **all four** `ActivitySource` names plus the `TracingInterceptor`:

```csharp
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using TemporalCommunity.Extensions.Agents;

const string mafTelemetrySource = "MyCompany.MyAgent";

// 1. Configure the OTel tracer provider with all relevant sources
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,      // Temporal client spans
        TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
        TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans
        TemporalAgentTelemetry.ActivitySourceName, // Temporal agent correlation spans
        mafTelemetrySource)                        // optional MAF GenAI spans
    .AddOtlpExporter()
    .Build();

// 2. Add the tracing interceptor to the Temporal client
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = "localhost:7233";
    opts.Interceptors = [new TracingInterceptor()];
});

// 3. Register agents as usual
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.ConfigureAgentPipeline = pipeline =>
                pipeline.UseOpenTelemetry(mafTelemetrySource);
        });
    });
```

> **Missing spans?** The four sources above produce the Temporal/library hierarchy. If you also
> configure MAF or MEAI OpenTelemetry middleware, add its explicit source name to `AddSource`.

---

## Span Reference

### `agent.client.send` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.SendAsync`

Wraps the full round-trip of sending an update to `AgentWorkflow` — from the external caller through `StartWorkflowAsync` + `ExecuteUpdateAsync` back to the response.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The registered agent name |
| `gen_ai.conversation.id` | The Temporal workflow ID (`ta-{name}-{key}`) |

**Error handling:** If the update fails, `span.SetStatus(ActivityStatusCode.Error, ex.Message)` is called.

### `agent.turn` (Client kind)

**Emitted by:** `AgentActivities.RunDurableAgentStepAsync`

Wraps one LLM-step activity. A user turn can produce multiple `agent.turn` spans: the initial model
call plus each follow-up after durable tool dispatch. This span is always retained, including when
MAF/MEAI telemetry is installed.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The registered agent name |
| `gen_ai.conversation.id` | The Temporal workflow ID |
| `temporal.agent.correlation_id` | Links the request to its response (from `RunRequest.CorrelationId`) |
| `gen_ai.usage.input_tokens` | Prompt tokens consumed when no upstream GenAI telemetry owns usage |
| `gen_ai.usage.output_tokens` | Completion tokens produced when no upstream GenAI telemetry owns usage |
| `gen_ai.usage.total_tokens` | Input + output tokens when no upstream GenAI telemetry owns usage |

The three usage attributes are set only when the provider reports usage and no live
`OpenTelemetryAgent`/`OpenTelemetryChatClient` is present. With upstream telemetry, its canonical
GenAI span owns usage so token/cost queries do not double-count.

### `agent.tool.invoke` (Internal kind)

**Emitted by:** `AgentActivities.InvokeAgentToolAsync`

Wraps one per-tool activity dispatch. Every `InvokeAgentTool` activity emits its own span, so a
turn with three tool calls in one round produces three `agent.tool.invoke` spans alongside the
`agent.turn` span for that round.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The registered agent name |
| `agent.tool.name` | The tool being invoked |
| `agent.tool.call_id` | The originating `FunctionCallContent.CallId`, when the model supplied one |

### Optional MAF/MEAI child spans

With MAF `OpenTelemetryAgent`, its sampled `invoke_agent` descendant receives the same
`temporal.agent.correlation_id` as `agent.turn`. The library selects the nearest active ancestor
whose `gen_ai.operation.name` is exactly `invoke_agent`; it never tags an arbitrary current span.
If the MAF source is unsampled, no such activity exists and enrichment is a safe no-op.

A standalone MEAI `OpenTelemetryChatClient` creates its chat span after the library's innermost
agent boundary. That child shares the `agent.turn` trace but does not inherit the Temporal tag;
correlation is therefore trace-based for this topology. It still owns usage attributes.

### `temporal.agent.schedule.create` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.ScheduleAgentAsync`

Wraps the creation of a recurring Temporal Schedule.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The agent being scheduled |
| `schedule.id` | The Temporal schedule ID |

### `temporal.agent.schedule.delayed` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.RunAgentDelayedAsync`

Wraps the creation of a delayed one-time agent session via `StartDelay`.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The agent being scheduled |
| `gen_ai.conversation.id` | The Temporal workflow ID |
| `schedule.delay` | The delay as `TimeSpan.ToString()` |

### `temporal.agent.schedule.one_time` (Internal kind)

**Emitted by:** `ScheduleActivities.ScheduleOneTimeAgentRunAsync`

Wraps a one-time scheduled run started from within a workflow via an activity.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The agent being scheduled |
| `schedule.job_id` | The run ID of the one-time job |
| `schedule.delay` | The delay before execution |

---

## Attribute Reference

All attributes are defined as constants on `TemporalAgentTelemetry`:

| Constant | Attribute Name | Type | Used In |
|----------|---------------|------|---------|
| `AgentNameAttribute` | `gen_ai.agent.name` | string | Agent spans |
| `AgentSessionIdAttribute` | `gen_ai.conversation.id` | string | Session-bearing spans |
| `AgentCorrelationIdAttribute` | `temporal.agent.correlation_id` | string | `agent.turn`, sampled MAF `invoke_agent` |
| `InputTokensAttribute` | `gen_ai.usage.input_tokens` | int? | Fallback `agent.turn` only |
| `OutputTokensAttribute` | `gen_ai.usage.output_tokens` | int? | Fallback `agent.turn` only |
| `TotalTokensAttribute` | `gen_ai.usage.total_tokens` | int? | Fallback `agent.turn` only |
| `AgentToolNameAttribute` | `agent.tool.name` | string | `agent.tool.invoke` |
| `AgentToolCallIdAttribute` | `agent.tool.call_id` | string | `agent.tool.invoke` (when the model supplied a call ID) |
| `ScheduleIdAttribute` | `schedule.id` | string | `schedule.create` |
| `ScheduleDelayAttribute` | `schedule.delay` | string | `schedule.delayed`, `schedule.one_time` |
| `ScheduleJobIdAttribute` | `schedule.job_id` | string | `schedule.one_time` |

---

## Full Span Hierarchy

A single `RunAsync` call from an external caller produces this trace:

```
agent.client.send                           ← TemporalAgentTelemetry (Client kind)
  │
  ├── StartWorkflow:AgentWorkflow           ← TracingInterceptor.ClientSource
  │
  └── UpdateWorkflow:RunAgent               ← TracingInterceptor.ClientSource
        │
        └── HandleUpdate:RunAgent           ← TracingInterceptor.WorkflowsSource
              │
              └── RunActivity:ExecuteAgent  ← TracingInterceptor.ActivitiesSource
                    │
                    └── agent.turn          ← Temporal correlation parent
                          │
                          └── invoke_agent  ← optional MAF canonical GenAI span
                                │
                                └── (LLM HTTP call, if instrumented)

  ... (if the turn produced tool calls, one InvokeAgentTool activity per call, each with its
       own agent.tool.invoke span) ...

  RunActivity:InvokeAgentTool                 ← TracingInterceptor.ActivitiesSource
        │
        └── agent.tool.invoke                ← one per tool call, fanned out via Workflow.WhenAllAsync
```

The two `TemporalAgentTelemetry` spans bookend the trace — `agent.client.send` at the top (caller-side) and `agent.turn` at the bottom (inference-side). The Temporal SDK spans fill in the middle, showing the workflow and activity execution. `agent.tool.invoke` spans are siblings of `agent.turn` — each `InvokeAgentTool` activity dispatched for that turn's tool calls gets its own span, separate from the LLM-step span.

---

## Search Attributes

Search attribute upserts are enabled by default. Set `EnableSearchAttributes = false` only when your cluster cannot register the required custom attributes:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
        // opts.EnableSearchAttributes = false; // explicit opt-out
    });
```

When enabled, `AgentWorkflow` upserts three [custom search attributes](https://docs.temporal.io/visibility#custom-search-attributes) on each workflow:

| Attribute | Type | When Updated |
|-----------|------|-------------|
| `AgentName` | Keyword | On workflow start |
| `SessionCreatedAt` | DateTimeOffset | On workflow start |
| `TurnCount` | Long | After each completed agent response |

### Registration for Production Clusters

For a local dev server, pass the attributes at startup:

```bash
temporal server start-dev \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int
```

For production clusters, register them once via the CLI:

```bash
temporal operator search-attribute create --name AgentName --type Keyword
temporal operator search-attribute create --name SessionCreatedAt --type Datetime
temporal operator search-attribute create --name TurnCount --type Int
```

### Example Queries in the Temporal UI

```
AgentName = "BillingAgent" AND TurnCount > 10
```

```
SessionCreatedAt > "2026-03-01T00:00:00Z"
```

```
AgentName = "WeatherAgent" AND ExecutionStatus = "Running"
```

### Querying via ListWorkflowsAsync

```csharp
var result = client.ListWorkflowsAsync(
    "AgentName = 'BillingAgent' AND TurnCount > 5");

await foreach (var execution in result)
{
    Console.WriteLine($"{execution.Id} — turns: {execution.SearchAttributes["TurnCount"]}");
}
```

---

## Correlating Across Continue-as-New

When `AgentWorkflow` triggers continue-as-new, the Temporal workflow ID stays the same but the run ID changes. Two mechanisms help correlate spans across these boundaries:

1. **Session ID (`gen_ai.conversation.id`)** — remains constant across continue-as-new transitions since it is the workflow ID
2. **Correlation ID (`temporal.agent.correlation_id`)** — set per-request on `RunRequest.CorrelationId`, allowing you to trace a single request across the boundary

To find all spans for a session regardless of which run they belong to, filter by
`gen_ai.conversation.id`. To trace a single request, use `temporal.agent.correlation_id` on the
Temporal turn and sampled MAF invoke span, or use trace identity for a standalone MEAI chat span.

---

## Operational Patterns

### Finding Expensive Agents by Token Count

Without upstream GenAI telemetry, filter `agent.turn` spans by
`gen_ai.usage.total_tokens`. With MAF/MEAI telemetry, query that middleware's canonical GenAI span
instead:

```
service.name = "my-agent-service"
AND name = "agent.turn"
AND gen_ai.usage.total_tokens > 10000
```

This surfaces turns where the LLM consumed an unusually high number of tokens — useful for identifying agents that need prompt optimization or context trimming.

### Monitoring Scheduling Spans

The three scheduling spans (`temporal.agent.schedule.create`, `temporal.agent.schedule.delayed`, `temporal.agent.schedule.one_time`) help monitor the health of scheduled agent runs. Errors on these spans indicate schedule creation failures — check for missing agents, invalid schedule specs, or Temporal server connectivity issues.

### Error Detection via Span Status

All agent spans set `ActivityStatusCode.Error` on failure with the exception message. Set up alerts in your tracing backend for:

```
status = ERROR AND name = "agent.client.send"
```

This catches agent invocation failures at the outermost layer — including workflow start failures, update rejections, and downstream agent errors.

### Latency Breakdown

Compare `agent.client.send` duration against `agent.turn` duration. The difference is the Temporal overhead (workflow scheduling, activity dispatch, serialization). In healthy systems this overhead is typically < 100ms; significantly higher values may indicate Temporal server pressure or large payload serialization.

### Per-LLM-Call Visibility

Each `agent.turn` span already represents one LLM-step activity. To add provider-semantic request,
response, model, and usage attributes, configure MAF `OpenTelemetryAgent` through
`ConfigureAgentPipeline` or return an MEAI `OpenTelemetryChatClient` from `agent.ChatClient`.

This is a doc-only pattern with no library opt-in flag. See [Per-LLM-Call Interception via `ChatClientFactory`](./llm-call-interception.md) for the full guide. It is the answer to "I want more visibility into when agents call the model and execute tools" — and it composes cleanly with the rest of the OTel setup described above.

---

## References

- `src/TemporalCommunity.Extensions.Agents/TemporalAgentTelemetry.cs` — all span and attribute constants
- `src/TemporalCommunity.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — `agent.client.send` and scheduling spans
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentActivities.cs` — `agent.turn` span with token metrics
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflow.cs` — search attribute upserts
- `samples/MAF/MultiAgentRouting/Program.cs` — complete OTel setup example
- [Temporal Visibility](https://docs.temporal.io/visibility) — search attribute documentation
- [Temporalio.Extensions.OpenTelemetry](https://github.com/temporalio/sdk-dotnet) — SDK tracing interceptor

---

_Last updated: 2026-09-05_
