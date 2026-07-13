# Observability

`TemporalCommunity.Extensions.AI` emits `System.Diagnostics.Activity` spans from the activity
source named `TemporalCommunity.Extensions.AI`. Register that source with your OpenTelemetry
tracer provider. If you also use Temporal's `TracingInterceptor`, register its client, workflow,
and activity sources to capture the Temporal scheduling envelope.

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(TracingInterceptor.ClientSource.Name)
        .AddSource(TracingInterceptor.WorkflowsSource.Name)
        .AddSource(TracingInterceptor.ActivitiesSource.Name)
        .AddConsoleExporter());
```

The library creates these spans:

| Operation | Span name | When it is emitted |
|---|---|---|
| Client request | `durable_chat.send` | `DurableChatSessionClient.SendAsync` |
| Model step | `chat {modelId}` | `GetResponse` (direct adapter) or `GetChatStep` (managed session) activity |
| Tool invocation | `execute_tool {toolName}` | Each `InvokeFunction` activity |

The model-step span has `gen_ai.operation.name=chat`, `conversation.id`, request and response
model IDs, and usage counts when the provider supplies them. A tool span has
`gen_ai.operation.name=execute_tool` and `gen_ai.tool.name`. `durable_chat.send` carries the
conversation ID, requested model, and final response usage.

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

## Durable history metadata

`GetHistoryAsync` returns request and response entries. Response entries include usage and both
entries share a correlation ID, so history can support per-turn audit and test assertions without
requiring an OpenTelemetry backend. Use traces for cross-process operational analysis; use history
for durable session state.

See the [OpenTelemetry sample](../../../samples/MEAI/OpenTelemetry/README.md) for a runnable
configuration.
