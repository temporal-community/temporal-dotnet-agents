# OpenTelemetry: Distributed Tracing for Durable Chat

## Overview

This sample demonstrates OpenTelemetry tracing and metrics for a durable MEAI chat session. It
produces a complete span hierarchy from the external `SendAsync` call, through the Temporal
protocol layers, down to the LLM inference span emitted by `DurableChatActivities`. It also shows
how to subscribe to the library's stable `Meter` name and the
`DurableAIPlugin` registration path — the plugin-based entry point — as an alternative to
`AddDurableAI()`.

- Full span hierarchy from `durable_chat.send` down through the Temporal protocol spans to the
  `chat {modelId}` inference span
- `conversation.id` attribute on the client send span and the inference span — filter an entire
  session in one query
- Four `ActivitySource` names must be registered with the tracer provider
- `DurableChatTelemetry.MeterName` must be registered with the meter provider to collect durable
  toolset resolver and validation measurements
- `TracingInterceptor` propagates the W3C `traceparent` header across gRPC boundaries
- Plugin registration path: `AddWorkerPlugin(new DurableAIPlugin(...))` as an alternative to
  `AddDurableAI()`

## Span Hierarchy

The library and the Temporal SDK emit spans during a chat turn:

- `TemporalCommunity.Extensions.AI` — library spans for the client send and the LLM inference call
- `Temporalio-Client`, `Temporalio-Workflow`, `Temporalio-Activity` — Temporal SDK protocol spans
  (emitted by `TracingInterceptor`)

The operation sequence for a single `SendAsync` call:

```
durable_chat.send                                                ← DurableChatTelemetry (client)
  └─ UpdateWorkflow:Chat                                         ← TracingInterceptor (client)
       └─ RunWorkflow:TemporalCommunity.Extensions.AI.DurableChatWorkflow
                                                                 ← TracingInterceptor (worker)
            └─ RunActivity:TemporalCommunity.Extensions.AI.GetChatStep  ← TracingInterceptor (activity)
                 └─ chat gpt-4o-mini                             ← DurableChatTelemetry
                                                                   (ActivityKind.Client)
                                                                   carries gen_ai.* tags
```

Notes on the names:

- `durable_chat.send` is a constant. Defined as `DurableChatTelemetry.ChatSendSpanName`.
- `UpdateWorkflow:{updateName}`, `RunWorkflow:{workflowType}`, and `RunActivity:{activityName}` are
  formatted by the Temporal SDK's `TracingInterceptor` from the workflow/update/activity names
  registered in code. The workflow type is `TemporalCommunity.Extensions.AI.DurableChatWorkflow`; the
  update is `Chat`; the model-step activity is `TemporalCommunity.Extensions.AI.GetChatStep`.
- `chat {modelId}` is constructed dynamically at the call site. The prefix
  (`DurableChatTelemetry.ChatOperationName`) follows the OTel GenAI semantic convention
  `"{operation.name} {model}"`. With `OPENAI_MODEL=gpt-4o-mini` this prints as `chat gpt-4o-mini`.

## Attributes

### On `durable_chat.send` (client send span)

| Attribute | Source |
|---|---|
| `conversation.id` | The session ID you pass to `SendAsync` |
| `gen_ai.request.model` | `ChatOptions.ModelId` from the request, if set |

### On `chat {modelId}` (LLM inference span)

| Attribute | Source |
|---|---|
| `gen_ai.operation.name` | Always `"chat"` |
| `conversation.id` | The session ID |
| `gen_ai.request.model` | `ChatOptions.ModelId` from the request |
| `gen_ai.response.model` | Model ID echoed by the response |
| `gen_ai.usage.input_tokens` | Token counts populated when the provider returns usage data |
| `gen_ai.usage.output_tokens` | Token counts populated when the provider returns usage data |

The Temporal SDK spans (`UpdateWorkflow:Chat`, `RunWorkflow:…`, `RunActivity:…`) carry the SDK's
own tags (workflow ID, run ID, attempt, namespace, etc.) — see the `TracingInterceptor` source for
the full list.

## Highlights

- **Four sources, not one.** `DurableChatTelemetry.ActivitySourceName` covers library semantic
  spans; the three `TracingInterceptor` sources cover Temporal protocol spans. Omitting any one of
  them produces gaps in your trace.

  ```csharp
  builder.Services
      .AddOpenTelemetry()
      .WithTracing(tracing => tracing
          .AddSource(DurableChatTelemetry.ActivitySourceName)
          .AddSource(TracingInterceptor.ClientSource.Name)
          .AddSource(TracingInterceptor.WorkflowsSource.Name)
          .AddSource(TracingInterceptor.ActivitiesSource.Name)
          .AddConsoleExporter())
      .WithMetrics(metrics => metrics
          .AddMeter(DurableChatTelemetry.MeterName)
          .AddConsoleExporter());
  ```
- **The library owns semantic diagnostics; the application owns export.**
  `TemporalCommunity.Extensions.AI` emits `ActivitySource`, `Meter`, and structured `ILogger`
  signals without depending on an OpenTelemetry SDK. `Temporalio.Extensions.OpenTelemetry`
  supplies the Temporal protocol spans and trace-context propagation. This sample selects the
  OpenTelemetry console exporters for both library spans and metrics.
- **`TracingInterceptor` is required for connected traces.** Without it, Temporal's internal gRPC
  calls break the distributed trace and the library spans appear disconnected from the protocol
  spans in your backend.
- **`conversation.id` makes session filtering practical.** Both the client-side `durable_chat.send`
  span and the worker-side `chat {modelId}` span carry `conversation.id`, so a single attribute
  filter surfaces every span for a session across all service instances.
- **`DurableAIPlugin` is the plugin entry point.** Gated by `[Experimental("TAI001")]`, it is
  equivalent to `AddDurableAI()` and follows the canonical Temporal AI Partner Ecosystem
  integration pattern. Suppress `TAI001` with `#pragma warning disable TAI001`.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/OpenTelemetry
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/OpenTelemetry

# Optional — defaults shown
dotnet user-secrets set "OPENAI_MODEL" "gpt-4o-mini"            --project samples/MEAI/OpenTelemetry
dotnet user-secrets set "TEMPORAL_ADDRESS" "localhost:7233"     --project samples/MEAI/OpenTelemetry
```

`OPENAI_API_KEY` and `OPENAI_API_BASE_URL` are required; the sample throws at startup if either is
unset. `OPENAI_MODEL` defaults to `gpt-4o-mini` and `TEMPORAL_ADDRESS` defaults to `localhost:7233`.

### Run

```bash
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj
```

### Expected Output

Span and metric data are written to the console by `AddConsoleExporter()`. You will see one set of
spans per chat turn (the sample makes two). Worker-owned toolset sessions additionally report the
attempt-scoped `temporal.ai.toolset.*` instruments described in
[`docs/how-to/MEAI/observability.md`](../../../docs/how-to/MEAI/observability.md). The workflow ID is
`chat-otel-demo-<guid>` — the `chat-` prefix
comes from `DurableExecutionOptions.WorkflowIdPrefix` (default) and the sample appends
`otel-demo-{Guid.NewGuid():N}` as the conversation ID, where `<guid>` is a 32-character hex string
with no hyphens. Look for entries roughly like the following (exact attribute formatting depends
on the OpenTelemetry version):

```
Activity.DisplayName: durable_chat.send
    Tags:
        conversation.id: otel-demo-<guid>
        gen_ai.request.model: gpt-4o-mini

Activity.DisplayName: UpdateWorkflow:Chat
    Tags:
        temporalWorkflowID: chat-otel-demo-<guid>
        ...

Activity.DisplayName: RunWorkflow:TemporalCommunity.Extensions.AI.DurableChatWorkflow
    Tags: ...

Activity.DisplayName: RunActivity:TemporalCommunity.Extensions.AI.GetChatStep
    Tags: ...

Activity.DisplayName: chat gpt-4o-mini
    Tags:
        gen_ai.operation.name: chat
        conversation.id: otel-demo-<guid>
        gen_ai.request.model: gpt-4o-mini
        gen_ai.response.model: gpt-4o-mini-2024-07-18
        gen_ai.usage.input_tokens: 42
        gen_ai.usage.output_tokens: 18
```

Filter by `conversation.id = otel-demo-<guid>` to see all spans for the session.

## What's new in the Temporal SDK protocol spans (1.14.x)

The Temporal .NET SDK's `TracingInterceptor` emits a fixed set of protocol-span name formats:
`StartWorkflow:{wf}`, `UpdateWorkflow:{update}`, `SignalWorkflow:{signal}`, `QueryWorkflow:{query}`,
`RunWorkflow:{wf}`, `CompleteWorkflow:{wf}`, `StartActivity:{act}`, `RunActivity:{act}`,
`HandleSignal:{sig}`, `HandleQuery:{q}`, `HandleUpdate:{u}`, `ValidateUpdate:{u}`. Recent additions
include `UpdateWithStartWorkflow:{wf}`, `SignalWithStartWorkflow:{wf}`, and the Nexus span family
(`StartNexusOperation:`, `RunStartNexusOperationHandler:`, `RunCancelNexusOperationHandler:`). The
Nexus-related sources are not exercised by this sample — it only performs `UpdateWorkflow` calls
against a single workflow.

## Durable tool callout

This sample does not register durable tools, so every chat turn dispatches a single
`TemporalCommunity.Extensions.AI.GetChatStep` model activity. If you register tools with
`AddDurableTools(...)`:

- The workflow schedules one `TemporalCommunity.Extensions.AI.GetChatStep` activity per LLM step
  (still wrapped in `RunActivity:...`, with the inner `chat {modelId}` span carrying `gen_ai.*` tags).
- Each tool invocation produces a separate `RunActivity:TemporalCommunity.Extensions.AI.InvokeFunction`
  span — one span per tool call.

See `docs/how-to/MEAI/tool-functions.md` for the managed durable-tool contract.

## Going to Production

This sample uses `AddConsoleExporter()` so you can see spans without running a collector. To ship
to a real backend like Jaeger, Tempo, Honeycomb, Datadog, or Grafana Cloud, uncomment
`.AddOtlpExporter()` in `Program.cs` and set `OTEL_EXPORTER_OTLP_ENDPOINT` (default OTLP/gRPC port
is `4317`; OTLP/HTTP is `4318`). Before you do, read the four warnings below — they are ordered by
risk.

**Going to production — read this first.**

1. **Do not capture request/response bodies.** `AddHttpClientInstrumentation()` does **not**
   capture LLM prompts or completions by default — it records method, URL, status, and headers
   only. If you add an `EnrichWithHttpRequestMessage`/`EnrichWithHttpResponseMessage` callback that
   reads `Content`, you will exfiltrate the full prompt and assistant output to your tracing
   backend. Don't.
2. **Treat `conversation.id` as a join key.** The sample uses random GUIDs, but real applications
   often substitute customer or tenant IDs. Hash or salt them before they leave the process;
   assume your tracing backend's access scope is broader than your application database.
3. **OTLP transport is plaintext unless you configure TLS.** Set
   `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` only over `https://` URLs in production. SaaS
   backends (Honeycomb, Datadog, Grafana Cloud) require `OTEL_EXPORTER_OTLP_HEADERS` for auth —
   these headers leak via `ps`, container metadata endpoints, and crash dumps. Inject via secrets
   manager, not shell env.
4. **Trace IDs are persisted in Temporal workflow history.** If traces are routed to a backend
   outside your data-residency boundary, those workflow histories still contain trace IDs that
   link back. Flag this to compliance during backend selection.

### OpenAI SDK instrumentation is experimental

`Program.cs` enables the OpenAI .NET SDK's OpenTelemetry instrumentation via
`AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true)` and registers the
`OpenAI.*` `ActivitySource` names with the tracer provider. This instrumentation ships
experimental and off by default; behaviour, span names, and attribute schemas may change in future
SDK versions. The source of truth is the
[OpenAI .NET Observability docs](https://github.com/openai/openai-dotnet/blob/main/docs/Observability.md).

The `chat {modelId}` inference span documented above is emitted by `DurableChatTelemetry` and is
not affected by the OpenAI switch — the switch adds additional, lower-level spans from inside the
OpenAI client. The Nexus span family mentioned in the "What's new in the Temporal SDK protocol
spans (1.14.x)" section above is also independent of this switch.
