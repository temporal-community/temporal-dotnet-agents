# Observability

`TemporalCommunity.Extensions.AI` uses the platform `ActivitySource`, `Meter`, and `ILogger`
primitives. Both diagnostics sources are named `TemporalCommunity.Extensions.AI`. The production
library has no OpenTelemetry SDK or exporter dependency; applications choose how to collect and
export these signals.

### OpenTelemetry setup

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

`Temporalio.Extensions.OpenTelemetry` supplies the Temporal client/workflow/activity tracing
interceptor and cross-process context propagation. It does not implement the library's toolset
metrics. Register both when you need the complete scheduling envelope and semantic AI signals.
Add `TracingInterceptor` to the Temporal client; workers created from that client use its tracing
interceptor as well.

### W3C propagator

Use a propagator that carries W3C trace context. OpenTelemetry SDK hosting usually configures a
default propagator, but when the process has no SDK-provided default, set it explicitly:

```csharp
var tracingInterceptor = new TracingInterceptor(new TracingInterceptorOptions
{
    Propagator = new TraceContextPropagator(),
});
```

An update or signal caller and its workflow handler are not guaranteed to form one parent/child
span chain. The SDK links `HandleUpdate:{name}` and `HandleSignal:{name}` handler spans to their
client-side `UpdateWorkflow:{name}` and `SignalWorkflow:{name}` spans respectively, while each
handler remains rooted in the workflow's original trace context. Both sides include the Temporal
workflow ID by default. Activity retries and workflow-task retries may produce additional spans;
tracing is diagnostic, not an exactly-once audit record.

### Workflow diagnostic

For a custom workflow diagnostic scope, use the SDK's replay-safe
`TrackWorkflowDiagnosticActivity` extension rather than calling `ActivitySource.StartActivity()`
from workflow code. It records a workflow diagnostic context immediately and lets SDK-created child
operations inherit it; it does not keep an exported span open. Keep the scope around bounded work,
not approval or idle waits:

```csharp
using System.Diagnostics;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Workflows;

[Workflow]
public class DiagnosticWorkflow
{
    private static readonly ActivitySource Diagnostics = new("MyApp.DurableWorkflow");

    [WorkflowRun]
    public async Task RunAsync()
    {
        using (Diagnostics.TrackWorkflowDiagnosticActivity("durable_chat.model_step"))
        {
            await Workflow.ExecuteActivityAsync(
                (MyActivities activity) => activity.DoThingAsync(),
                new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }
    }
}
```

The Temporal workflow ID is the stable correlation key on SDK spans. The library's `durable_chat.send` span and tool spans use the application session ID in
`gen_ai.conversation.id`; when a workflow ID prefix is configured, this value is related to the
Temporal workflow ID by that prefix rather than being identical.

The library creates these spans:

| Operation | Span name | When it is emitted |
|---|---|---|
| Client request | `durable_chat.send` | `DurableChatSessionClient.SendAsync` |
| Tool invocation | `execute_tool {toolName}` | Each `InvokeFunction` activity |
| Toolset resolution | `durable_toolset.resolve` | Once for a new worker-owned session baseline |

The library does not create inference spans or copy token usage onto its Temporal update span.
Inference spans and token metrics come from an application-configured MEAI
`OpenTelemetryChatClient`; without one, inference telemetry is not emitted by this package:

### MEAI inference instrumentation

```csharp
builder.Services
    .AddChatClient(providerChatClient)
    .UseOpenTelemetry()
    .Build();
```

`UseOpenTelemetry()` follows the pinned MEAI GenAI conventions. Its content-capture default comes
from `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`; MEAI 10.10.0 emits its spans from
`Experimental.Microsoft.Extensions.AI`, which must be added to the tracer provider's sources. Set
`DurableExecutionOptions.InferenceTelemetryMode` to
`RequireVisibleOpenTelemetryClient` to reject a resolved chat-client chain unless it contains
exactly one discoverable MEAI wrapper. Chain discovery follows `DelegatingChatClient` inner links.
If an application-owned opaque client hides an already configured wrapper, explicitly select
`TrustOpaqueApplicationInstrumentation`; this trusts the application and does not add another
wrapper. The default `ApplicationOwned` mode leaves inference instrumentation entirely to the
application. These modes verify configuration only; the library never wraps the provider client for
inference telemetry.

### Telemetry content capture

The worker's `CaptureTelemetryContent` option provides one policy for visible MEAI inference
wrappers and library-owned tool spans. When unset, it follows MEAI's
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` default (`true`, case-insensitive, enables
capture; otherwise content stays redacted). An explicit value overrides the environment default
and configures a discoverable `OpenTelemetryChatClient` to match:

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options => options.CaptureTelemetryContent = false);
```

For an opaque application-owned client chain, set both
`InferenceTelemetryMode = TrustOpaqueApplicationInstrumentation` and
`CaptureTelemetryContent` explicitly, and configure the hidden wrapper to the same value. The
library cannot inspect or change an opaque wrapper; it fails configuration rather than claiming
the two policies match. A visible wrapper whose explicitly configured setting differs from the
environment default also requires an explicit library override.

When enabled, the library's `execute_tool` span adds JSON-serialized
`gen_ai.tool.call.arguments` and `gen_ai.tool.call.result` attributes, each bounded to 4096
characters. Unsupported or unserializable values are omitted from telemetry; large values are
marked as truncated. Serialization failures produce a content-free warning and do not fail an
already completed tool effect.
When disabled, these attributes are absent, and the MEAI wrapper redacts message bodies. Streamed
assistant text is never included in Temporal activity heartbeat details. Content capture is a
sensitive-data policy choice, not a storage or payload-codec setting: exporters, access controls,
sampling, and retention still apply.

Long-running buffered history summarization and asynchronous context-reducer preparation send
detail-free activity heartbeats while awaiting the provider or reducer. These heartbeats keep the
configured heartbeat timeout active without including conversation content.

A tool span uses `ActivityKind.Internal` and includes `gen_ai.operation.name=execute_tool`,
`gen_ai.tool.name`, `gen_ai.tool.type=function`, and the available
`gen_ai.tool.call.id`, `gen_ai.tool.description`, and `gen_ai.conversation.id`. Call IDs,
descriptions, conversation IDs, and error types are length-bounded. Arguments and successful
results are additional content attributes only when the capture policy is enabled; exception
messages are never span attributes. Failed tool spans use a bounded `error.type` and an error status
without an exception-message description.

`CaptureTelemetryContent` also governs exception detail in package-owned activity failure logs.
When capture is disabled, an activity failure emits one sanitized Error entry with the exception
type, bounded error type, available HTTP status, and non-content operation context; its full
exception is available only at Debug. When capture is enabled, the full exception remains at Error.
This avoids duplicate failure entries for one activity attempt. The policy does not redact logs
written directly by providers or application code; configure those sinks, access controls, and
retention separately.

Workflow outcome counters use Temporal's replay-safe `Workflow.MetricMeter`; they are not emitted
through `DurableChatTelemetry.MeterName`. Configure the Temporal runtime metrics sink separately
if you need these counters exported. For example, the pinned SDK supports its runtime OpenTelemetry
metrics exporter:

### Temporal workflow metrics exporter

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
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`; `CaptureTelemetryContent` can explicitly
override it for the durable library's visible wrapper and tool spans. Treat enabling capture as a
sensitive-data policy change: prompts, responses, tool arguments, and tool results may appear in
telemetry.

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
