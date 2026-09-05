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
| Model step | `chat {modelId}` | `GetResponse` (direct adapter) or `GetChatStep` (managed session) activity |
| Tool invocation | `execute_tool {toolName}` | Each `InvokeFunction` activity |
| Toolset resolution | `durable_toolset.resolve` | Once for a new worker-owned session baseline |

The model-step span has `gen_ai.operation.name=chat`, `conversation.id`, request and response
model IDs, and usage counts when the provider supplies them. A tool span has
`gen_ai.operation.name=execute_tool` and `gen_ai.tool.name`. `durable_chat.send` carries the
conversation ID, requested model, and final response usage.

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
  → GetChatStep activity → chat {modelId}
  → InvokeFunction activity → execute_tool {toolName}   (for each tool call)
  → GetChatStep activity → chat {modelId}               (until final response)
```

The direct `DurableChatClient` adapter uses the separate `GetResponse` activity and does not
implement the managed-session tool loop.

See the [OpenTelemetry sample](../../../samples/MEAI/OpenTelemetry/README.md) for a runnable
configuration.
