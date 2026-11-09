# OpenTelemetry: Distributed Tracing for Durable Chat

## Overview

This sample demonstrates OpenTelemetry tracing and metrics for a durable MEAI chat session. It
produces a complete span hierarchy from the external `SendAsync` call, through the Temporal
protocol layers, down to the LLM inference span emitted by MEAI's application-configured
`OpenTelemetryChatClient`. It also shows how to subscribe to the library's stable `Meter` name.

- Full span hierarchy from `durable_chat.send` through Temporal protocol spans to the MEAI
  `chat {model}` inference span
- `gen_ai.conversation.id` on the client send span; the application-owned MEAI inference span uses
  GenAI attributes and may be correlated through the Temporal trace
- Five core `ActivitySource` names must be registered with the tracer provider (the optional
  OpenAI SDK instrumentation adds its own source)
- `DurableChatTelemetry.MeterName` must be registered with the meter provider to collect durable
  toolset resolver and validation measurements
- Workflow outcome counters use Temporal's replay-safe workflow metric meter and require the
  Temporal runtime's metrics exporter; this sample exposes them at
  `http://127.0.0.1:9090/metrics`. Adding the library meter alone does not collect them
- `TracingInterceptor` propagates the W3C `traceparent` header across gRPC boundaries

## Span Hierarchy

The library and the Temporal SDK emit spans during a chat turn:

- `TemporalCommunity.Extensions.AI` — Temporal update and toolset spans
- `execute_tool {toolName}` — internal GenAI tool-operation span with bounded call/session
  metadata; tool arguments and results are absent unless telemetry content capture is enabled
- MEAI `OpenTelemetryChatClient` — provider inference spans and token metrics
- `Temporalio-Client`, `Temporalio-Workflow`, `Temporalio-Activity` — Temporal SDK protocol spans
  (emitted by `TracingInterceptor`)

The operation sequence for a single `SendAsync` call:

```
durable_chat.send                                                ← DurableChatTelemetry (client)
  └─ UpdateWorkflow:Chat                                         ← TracingInterceptor (client)
       └─ RunWorkflow:TemporalCommunity.Extensions.AI.DurableChatWorkflow
                                                                 ← TracingInterceptor (worker)
            └─ RunActivity:TemporalCommunity.Extensions.AI.GetChatStep  ← TracingInterceptor (activity)
                 └─ chat gpt-4o-mini                             ← MEAI OpenTelemetryChatClient
                                                                   carries gen_ai.* tags
```

Notes on the names:

- `durable_chat.send` is a constant. Defined as `DurableChatTelemetry.ChatSendSpanName`.
- `UpdateWorkflow:{updateName}`, `RunWorkflow:{workflowType}`, and `RunActivity:{activityName}` are
  formatted by the Temporal SDK's `TracingInterceptor` from the workflow/update/activity names
  registered in code. The workflow type is `TemporalCommunity.Extensions.AI.DurableChatWorkflow`; the
  update is `Chat`; the model-step activity is `TemporalCommunity.Extensions.AI.GetChatStep`.
- `chat {model}` is emitted by MEAI's `OpenTelemetryChatClient` and follows its pinned GenAI
  semantic-convention implementation. The library does not emit a second inference span.

## Attributes

### On `durable_chat.send` (client send span)

| Attribute | Source |
|---|---|
| `gen_ai.conversation.id` | The session ID you pass to `SendAsync` |
| `temporal.ai.chat.requested_model` | `ChatOptions.ModelId` from the request, if set |

### On `chat {model}` (LLM inference span)

| Attribute | Source |
|---|---|
| `gen_ai.operation.name` | Always `"chat"` |
| `gen_ai.request.model` | `ChatOptions.ModelId` from the request |
| `gen_ai.response.model` | Model ID echoed by the response |
| `gen_ai.usage.input_tokens` | Token counts populated when the provider returns usage data |
| `gen_ai.usage.output_tokens` | Token counts populated when the provider returns usage data |

The Temporal SDK spans (`UpdateWorkflow:Chat`, `RunWorkflow:…`, `RunActivity:…`) carry the SDK's
own tags (workflow ID, run ID, attempt, namespace, etc.) — see the `TracingInterceptor` source for
the full list.

## Highlights

- **Five sources, not one.** `DurableChatTelemetry.ActivitySourceName` covers library semantic
  spans; the three `TracingInterceptor` sources cover Temporal protocol spans; and MEAI's source
  covers inference. Omitting a source produces gaps in your trace.

  ```csharp
  builder
      .AddOpenTelemetry()
      .WithTracing(tracing => tracing
          .AddSource(DurableChatTelemetry.ActivitySourceName)
          .AddSource(TracingInterceptor.ClientSource.Name)
          .AddSource(TracingInterceptor.WorkflowsSource.Name)
          .AddSource(TracingInterceptor.ActivitiesSource.Name)
          .AddSource("Experimental.Microsoft.Extensions.AI")
          .AddConsoleExporter())
      .WithMetrics(metrics => metrics
          .AddMeter("Experimental.Microsoft.Extensions.AI")
          .AddMeter(DurableChatTelemetry.MeterName)
          .AddConsoleExporter());
  ```
- **Inference instrumentation is application-owned.** The sample configures
  `.UseOpenTelemetry()` on the provider chat client; the durable library does not add inference
  spans or duplicate token usage tags. `Temporalio.Extensions.OpenTelemetry` supplies the Temporal
  protocol spans and trace-context propagation.
- **Content capture is explicitly disabled in this sample.** `CaptureTelemetryContent = false`
  keeps MEAI message bodies and library tool argument/result attributes out of spans even if the
  MEAI environment variable enables capture. If unset, the option follows
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`. For opaque app-owned wrappers, set the
  option explicitly and configure the wrapper to the same policy.
- **`TracingInterceptor` is required for connected traces.** Without it, Temporal's internal gRPC
  calls break the distributed trace and the library spans appear disconnected from the protocol
  spans in your backend. Configure a W3C `TraceContextPropagator` when your host has not configured
  an OpenTelemetry default propagator; this sample does so explicitly.
- **Update handlers are linked, not necessarily nested.** `HandleUpdate:{name}` spans link to the
  corresponding client `UpdateWorkflow:{name}` span while remaining rooted in the workflow's
  original trace context. Both carry the Temporal workflow ID. Retries can emit additional spans;
  traces are not an exactly-once audit record.
- **Custom workflow diagnostics must be replay-safe.** Use
  `TrackWorkflowDiagnosticActivity` around bounded work; never call
  `ActivitySource.StartActivity()` in workflow code or keep a diagnostic scope across approval or
  idle waits. The extension records workflow diagnostic context, not a long-lived exported span.
- **The library's source is not the inference source.** Add the MEAI wrapper's source when
  collecting inference spans; the exact trace parent layout depends on cross-process propagation.
- **`AddDurableAI()` is the registration entry point.** The package ships no plugin-registration
  wrappers; to add your own Temporal plugins, populate `TemporalWorkerOptions.Plugins` or
  `TemporalClientConnectOptions.Plugins` directly.

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

The Generic Host loads user secrets only in Development. Set `DOTNET_ENVIRONMENT` for this run to
use the credentials configured above.

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj
```

PowerShell:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj
```

### Expected Output

Spans and .NET `Meter` measurements are written to the console by `AddConsoleExporter()`. Temporal
workflow metrics are available at `http://127.0.0.1:9090/metrics`. You will see one set of spans
per chat turn (the sample makes two). Worker-owned toolset sessions additionally report the
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
        gen_ai.conversation.id: otel-demo-<guid>

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
        gen_ai.request.model: gpt-4o-mini
        gen_ai.response.model: gpt-4o-mini-2024-07-18
        gen_ai.usage.input_tokens: 42
        gen_ai.usage.output_tokens: 18
```

Filter send spans by `gen_ai.conversation.id = otel-demo-<guid>`, then follow their Temporal
traces to the inference spans. Do not assume every leaf span carries the application session ID.

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
  (still wrapped in `RunActivity:...`, with an application-configured `chat {model}` span carrying
  `gen_ai.*` tags).
- Each tool invocation produces a separate `RunActivity:TemporalCommunity.Extensions.AI.InvokeFunction`
  span — one span per tool call — plus an `execute_tool {toolName}` internal span with
  `gen_ai.operation.name`, `gen_ai.tool.name`, `gen_ai.tool.type`, and available bounded call ID,
  description, and conversation ID attributes. Failure spans have a bounded `error.type`; exception
  messages are never attributes. Arguments and successful results are added only when
  `CaptureTelemetryContent` is enabled and are bounded to 4096 characters.

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
2. **Treat `gen_ai.conversation.id` on send spans as a join key.** The sample uses random GUIDs, but real applications
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

The `chat {model}` inference span documented above is emitted by MEAI's configured
`OpenTelemetryChatClient`. The OpenAI switch adds additional provider-level spans from inside the
OpenAI client; those may coexist with the MEAI inference span and should be considered when
counting provider attempts.
