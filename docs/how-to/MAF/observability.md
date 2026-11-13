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

When the relevant sources are enabled and sampled, these layers compose into a trace from the
client call through workflow and activity execution to the model invocation.

By default, `AgentWorkflow` upserts **search attributes** on each workflow run, enabling operational queries in the Temporal Web UI and via the `ListWorkflowsAsync` API. Set `EnableSearchAttributes = false` to opt out.

---

## Setup

Install the required packages:

```bash
dotnet add package Temporalio.Extensions.OpenTelemetry
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol  # or your preferred exporter
```

Subscribe to the three Temporal SDK sources and the Temporal Agents source. If you also configure
MAF or MEAI telemetry, subscribe to that middleware's source as well:

In an existing host builder with an `IChatClient` registered in DI:

```csharp
using Microsoft.Agents.AI;          // pipeline.UseOpenTelemetry — an AIAgentBuilder extension
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;                // Sdk.CreateTracerProviderBuilder
using OpenTelemetry.Trace;
using Temporalio.Extensions.Hosting;
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
    opts.Namespace = "default";
    opts.Interceptors = [new TracingInterceptor()];
});

// 3. Register agents as usual. Register an IChatClient in DI for this agent.
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

> **Missing spans?** The four Temporal SDK/library sources produce the Temporal hierarchy. If you
> also configure MAF or MEAI OpenTelemetry middleware, add its explicit source name to `AddSource`.

---

## Span Reference

### `agent.client.send` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.SendAsync`

Wraps the `ExecuteUpdateWithStartWorkflowAsync` call that starts the workflow if needed, sends its
`RunAgentAsync` update, and waits for the response.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The registered agent name |
| `gen_ai.conversation.id` | The Temporal workflow ID (`ta-{name}-{key}`) |

### `agent.turn` (Client kind)

**Emitted by:** `AgentActivities.RunDurableAgentStepAsync`

Records the agent run and result processing inside one model-step activity. A user turn can produce
multiple `agent.turn` spans: the initial model call plus each follow-up after durable tool dispatch.
The span is emitted independently of MAF/MEAI instrumentation, subject to normal OpenTelemetry
listener and sampling settings.

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

Wraps one per-tool activity dispatch. Each `InvokeAgentTool` activity creates its own span, subject
to normal OpenTelemetry listener and sampling settings. A round with three tool calls can therefore
produce three `agent.tool.invoke` spans alongside that round's `agent.turn` span.

| Attribute | Value |
|-----------|-------|
| `gen_ai.agent.name` | The registered agent name |
| `agent.tool.name` | The tool being invoked |
| `agent.tool.call_id` | The originating `FunctionCallContent.CallId`, when the model supplied one |

### MAF/MEAI spans and failure-log content

Package-owned activity failure logs for durable MAF agents use the shared
`CaptureTelemetryContent` policy. An explicitly set value on the scoped `DurableExecutionOptions`
overrides the `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` environment default; if the
value is unset or no options instance is registered, the environment default applies. When capture
is disabled, Error entries contain bounded exception and error type names, an HTTP status when
available, and non-content operation identifiers; the full exception is logged at Debug. When
capture is enabled, the full exception remains at Error. Provider and application logger output is
not controlled by this package policy.

With MAF `OpenTelemetryAgent`, its sampled `invoke_agent` descendant receives the same
`temporal.agent.correlation_id` as `agent.turn`. The library selects the nearest active ancestor
whose `gen_ai.operation.name` is exactly `invoke_agent`; it never tags an arbitrary current span.
If the MAF source is unsampled, no such activity exists and enrichment is a safe no-op.

A standalone MEAI `OpenTelemetryChatClient` creates its chat span after the library's innermost
agent boundary. That child shares the `agent.turn` trace but does not inherit the Temporal tag;
correlation is therefore trace-based for this topology. It still owns usage attributes.

## Attribute Reference

All attributes are defined as constants on `TemporalAgentTelemetry`:

| Constant | Attribute Name | Type | Used In |
|----------|---------------|------|---------|
| `AgentNameAttribute` | `gen_ai.agent.name` | string | Agent spans |
| `AgentSessionIdAttribute` | `gen_ai.conversation.id` | string | Session-bearing spans |
| `AgentCorrelationIdAttribute` | `temporal.agent.correlation_id` | string | `agent.turn`, sampled MAF `invoke_agent` |
| `InputTokensAttribute` | `gen_ai.usage.input_tokens` | `long?` | Fallback `agent.turn` only |
| `OutputTokensAttribute` | `gen_ai.usage.output_tokens` | `long?` | Fallback `agent.turn` only |
| `TotalTokensAttribute` | `gen_ai.usage.total_tokens` | `long?` | Fallback `agent.turn` only |
| `AgentToolNameAttribute` | `agent.tool.name` | string | `agent.tool.invoke` |
| `AgentToolCallIdAttribute` | `agent.tool.call_id` | string | `agent.tool.invoke` (when the model supplied a call ID) |

---

## Full Span Hierarchy

With the relevant sources enabled and sampled, a typical `SendAsync` call has this structure.
Each model/tool cycle can repeat:

```
agent.client.send                    ← TemporalAgentTelemetry
  └─ Temporal client/workflow spans  ← TracingInterceptor
       ├─ RunDurableAgentStep activity
       │    └─ agent.turn
       │         └─ optional MAF invoke_agent or MEAI chat span
       └─ InvokeAgentTool activity (when a tool is dispatched)
            └─ agent.tool.invoke
```

The Temporal SDK spans show workflow and activity execution. `agent.turn` and
`agent.tool.invoke` are emitted inside their respective activities; tool-dispatch spans are
separate from the model-step span. MAF/MEAI spans are optional and depend on the configured
instrumentation.

---

## Search Attributes

Search attribute upserts are enabled by default. Set `EnableSearchAttributes = false` to skip them
if you do not need these filters or cannot register the required custom attributes. In the existing
host builder from [Setup](#setup), registration looks like this:

```csharp
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = "localhost:7233";
    opts.Namespace = "default";
});

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
        // opts.EnableSearchAttributes = false; // explicit opt-out
    });
```

When enabled, `AgentWorkflow` upserts three [custom search attributes](https://docs.temporal.io/visibility#custom-search-attributes):

| Attribute | Temporal type (typed .NET key) | When updated |
|-----------|------------------------------------|--------------|
| `AgentName` | `Keyword` (`string`) | On workflow start |
| `SessionCreatedAt` | `Datetime` (`DateTimeOffset`) | On workflow start |
| `TurnCount` | `Int` (`long`) | On workflow start and after each completed agent response |

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

Given an `ITemporalClient` resolved from your application's dependency injection container:

```csharp
var result = client.ListWorkflowsAsync(
    "AgentName = 'BillingAgent' AND TurnCount > 5");

await foreach (var execution in result)
{
    Console.WriteLine(execution.Id);
}
```

---

## Correlating Across Continue-as-New

When `AgentWorkflow` continues as new, the workflow ID stays the same but the run ID changes.
The workflow ID is recorded as `gen_ai.conversation.id` on `agent.client.send` and `agent.turn`
spans, so those spans can be filtered by session across runs.

`temporal.agent.correlation_id` is set per request on `agent.turn` and on the sampled MAF
`invoke_agent` span. Use it to correlate those spans for one request; standalone MEAI chat spans
share the trace but do not inherit this attribute.

Filter `agent.client.send` or `agent.turn` by `gen_ai.conversation.id`, then inspect the trace for
related activity and tool spans. These attributes do not guarantee that a request's trace itself
continues across a continue-as-new boundary.

---

## Operational Patterns

### Finding Expensive Agents by Token Count

Without upstream GenAI telemetry, filter sampled `agent.turn` spans by
`gen_ai.usage.total_tokens` when the provider reports usage. With MAF/MEAI telemetry, query that
middleware's canonical GenAI span instead:

Example trace-search filter; syntax varies by backend:

```
service.name = "my-agent-service"
AND name = "agent.turn"
AND gen_ai.usage.total_tokens > 10000
```

This surfaces turns where the LLM consumed an unusually high number of tokens — useful for identifying agents that need prompt optimization or context trimming.

### Error Detection via Span Status

After they start, `agent.turn` sets error status when the model step throws and `agent.tool.invoke`
sets it for thrown failures and classified safe tool failures (returned to the workflow as tool
results). Failures during earlier activity setup may occur before either span starts.
`agent.client.send` does not set error status when the update call fails, so a status-only alert on
that span will miss client-side failures. Use Temporal SDK workflow/activity spans and package logs
alongside agent spans when monitoring failures.

### Latency Breakdown

`agent.client.send` measures the update round trip; each `agent.turn` measures one model-step
activity. Their durations cover different work and should not be subtracted as a direct measure of
Temporal overhead: the client span may include queueing, retries, tool activities, and approval
waits. Use the Temporal SDK spans to inspect workflow and activity timing.

### Per-LLM-Call Visibility

Each `agent.turn` span covers the agent run and result processing inside one model-step activity;
the activity's earlier context preparation is outside the span. To add provider-semantic request,
response, model, and usage attributes, configure MAF `OpenTelemetryAgent` through
`ConfigureAgentPipeline` or return an MEAI `OpenTelemetryChatClient` from `agent.ChatClient`.

See [Intercepting LLM Calls](./llm-call-interception.md) for configuration details.

---

## References

- `src/TemporalCommunity.Extensions.Agents/TemporalAgentTelemetry.cs` — all span and attribute constants
- `src/TemporalCommunity.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — `agent.client.send` spans
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentActivities.cs` — `agent.turn` span with token metrics
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflow.cs` and
  `src/TemporalCommunity.Extensions.AI/DurableChatWorkflowBase.cs` — search attribute upserts
- `samples/MAF/MultiAgentRouting/Program.cs` — complete OTel setup example
- [Temporal Visibility](https://docs.temporal.io/visibility) — search attribute documentation
- [Temporalio.Extensions.OpenTelemetry](https://github.com/temporalio/sdk-dotnet) — SDK tracing interceptor

---
