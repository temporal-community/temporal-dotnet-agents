# Observability

`TemporalCommunity.Extensions.AI` uses the platform `ActivitySource`, `Meter`, and `ILogger`
primitives. Both diagnostics sources are named `TemporalCommunity.Extensions.AI`. The production
library has no OpenTelemetry SDK or exporter dependency; applications choose how to collect and
export these signals.

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

`Temporalio.Extensions.OpenTelemetry` supplies the Temporal client/workflow/activity tracing
interceptor and cross-process context propagation. It does not implement the library's toolset
metrics. Register both when you need the complete scheduling envelope and semantic AI signals.

The library creates these spans:

| Operation | Span name | When it is emitted |
|---|---|---|
| Client request | `durable_chat.send` | `DurableChatSessionClient.SendAsync` |
| Tool invocation | `execute_tool {toolName}` | Each `InvokeFunction` activity |
| Toolset resolution | `durable_toolset.resolve` | Once for a new worker-owned session baseline |

The library does not create inference spans or copy token usage onto its Temporal update span.
Inference spans and token metrics come from an application-configured MEAI
`OpenTelemetryChatClient`; without one, inference telemetry is not emitted by this package:

```csharp
builder.Services
    .AddChatClient(providerChatClient)
    .UseOpenTelemetry()
    .Build();
```

`UseOpenTelemetry()` follows the pinned MEAI GenAI conventions and captures message content only
when explicitly enabled. Set `DurableExecutionOptions.InferenceTelemetryMode` to
`RequireVisibleOpenTelemetryClient` to reject a resolved chat-client chain unless it contains
exactly one discoverable MEAI wrapper. Chain discovery follows `DelegatingChatClient` inner links.
If an application-owned opaque client hides an already configured wrapper, explicitly select
`TrustOpaqueApplicationInstrumentation`; this trusts the application and does not add another
wrapper. The default `ApplicationOwned` mode leaves instrumentation entirely to the application.
These modes verify configuration only; the library never wraps the provider client for inference
telemetry.

A tool span uses `ActivityKind.Internal` and includes `gen_ai.operation.name=execute_tool`,
`gen_ai.tool.name`, `gen_ai.tool.type=function`, and the available
`gen_ai.tool.call.id`, `gen_ai.tool.description`, and `gen_ai.conversation.id`. Call IDs,
descriptions, conversation IDs, and error types are length-bounded. Tool arguments, results, and
exception messages are not span attributes. Failed tool spans use a bounded `error.type` and an
error status without an exception-message description.

Workflow outcome counters use Temporal's replay-safe `Workflow.MetricMeter`; they are not emitted
through `DurableChatTelemetry.MeterName`. Configure the Temporal runtime metrics sink separately
if you need these counters exported. For example, the pinned SDK supports its runtime OpenTelemetry
metrics exporter:

```csharp
using Temporalio.Runtime;

var runtime = new TemporalRuntime(new TemporalRuntimeOptions(new TelemetryOptions
{
    Metrics = new MetricsOptions(new OpenTelemetryOptions("http://localhost:4318")
    {
        MetricsExportInterval = TimeSpan.FromSeconds(30),
        Protocol = OpenTelemetryProtocol.Grpc,
    }),
}));

var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    Runtime = runtime,
});
```

The workflow counters use only bounded outcome/reason values:

| Instrument | Tags | Meaning |
|---|---|---|
| `temporal.ai.turn.outcomes` | `outcome=completed|failed|cancelled` | Terminal turn results |
| `temporal.ai.turn.admission.rejections` | `reason=busy|conflict|epoch_mismatch|admission_closed|history_blocked|history_budget|pending_limit` | Supported turn-admission rejections |
| `temporal.ai.approval.outcomes` | `outcome=approved|rejected|expired` | Completed approval waits |
| `temporal.ai.approval.resolutions` | `outcome=accepted|rejected` | Reviewer resolution updates |
| `temporal.ai.approval.expiries` | `reason=timeout|history_budget` | Deterministic approval expiry reasons |
| `temporal.ai.history.compaction.outcomes` | `outcome=success|failure` | Compaction attempts, including explicit recovery attempts |

These are operational workflow metrics, not durable audit or billing records. Temporal suppresses
workflow metric recording during replay; Activity metrics may still be emitted again on an activity
retry. The library `MeterName` remains the collection point for package-owned activity metrics such
as toolset resolution and validation.

MEAI's default content-capture behavior is controlled by
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` (unset/false means redacted). Applications
may explicitly set `OpenTelemetryChatClient.EnableSensitiveData`, which overrides that default.
Treat enabling capture as a sensitive-data policy change: prompts, responses, tool arguments, and
tool results may appear in telemetry.

## Toolset metrics and logs

| Instrument | Type | Meaning |
|---|---|---|
| `temporal.ai.toolset.resolver.attempts` | Counter | Completed resolver activity attempts, tagged only with `outcome=success|failure` |
| `temporal.ai.toolset.resolver.selected_toolsets` | Histogram | Number of selected groups on successful resolution |
| `temporal.ai.toolset.resolver.selected_functions` | Histogram | Number of selected functions on successful resolution |
| `temporal.ai.toolset.declaration_snapshot.size` | Histogram (`By`) | Serialized bytes in the once-per-session worker manifest |
| `temporal.ai.toolset.validation.rejections` | Counter | Activity-side rejection, tagged with one bounded reason |

Rejection reasons are restricted to `unknown_toolset`, `duplicate_selection`, `name_collision`,
`invalid_manifest_version`, `manifest_mismatch`, `authority_mismatch`, `invalid_declaration`, and
`invalid_policy`. Metrics and resolver spans never use toolset/function names, conversation or
tenant IDs, fingerprints, exception text, schemas, request data, or turn state as dimensions.
The snapshot-size histogram has no dimensions and is measured only when the resolver already has
the manifest; it does not reserialize declarations on each model activity.

These are attempt-scoped operational signals, not durable audit or billing counts. An activity
retry can emit another attempt, and process failure around measurement export can omit or duplicate
a signal. Temporal history remains the execution record. Workflow-side turn-narrowing rejection
uses the replay-safe workflow logger and a non-retryable failure; it does not create a span, metric,
or telemetry-only activity. `GetHistoryAsync` returns request and response entries — response
entries include usage, and both entries in a turn share a correlation ID — so history can support
per-turn audit and test assertions without requiring an OpenTelemetry backend.

| Owner | Signal | Responsibility |
|---|---|---|
| This library | `ActivitySource`, `Meter`, structured `ILogger` events | Semantic model/tool/toolset operations |
| Temporal .NET SDK interceptor | Client, workflow, and activity spans | Scheduling envelope and trace propagation |
| Application | OpenTelemetry SDK/exporters and log providers | Collection, sampling, routing, retention, and export |

## Managed-session trace shape

The exact parent/child layout depends on process boundaries and exporter propagation, so treat the
following as the operation sequence rather than a guaranteed span tree:

```
SendAsync
  → UpdateWorkflow:Chat
  → GetChatStep activity → MEAI chat {model} inference span (when configured)
  → InvokeFunction activity → execute_tool {toolName}   (for each tool call)
  → GetChatStep activity → MEAI chat {model} inference span (until final response)
```

The direct `DurableChatClient` adapter uses the separate `GetResponse` activity and does not
implement the managed-session tool loop.

See the [OpenTelemetry sample](../../../samples/MEAI/OpenTelemetry/README.md) for a runnable
configuration.
