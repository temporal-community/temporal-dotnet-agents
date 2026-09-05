# TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` adds Temporal durable execution to
[`Microsoft.Extensions.AI`](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). The
package references MEAI and the Temporal .NET SDK; it does not reference Microsoft Agent Framework.

## Prerequisites

- Temporal Service 1.31.0 or newer.
- .NET 10 SDK or later to build the repository and run its samples. The published package also
  ships a `netstandard2.1` asset.

## Quick start

Register the Temporal client and worker:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddChatClient(innerChatClient);

var weatherTool = AIFunctionFactory.Create(
    (string city) => weather.GetCurrent(city),
    name: "get_weather",
    description: "Gets current weather for a city.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.SessionTimeToLive = TimeSpan.FromHours(24);
        options.MaxToolCallsPerTurn = 10;
    })
    .AddDurableTool(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
```

`AddTemporalClient` auto-wires the MEAI-aware data converter, so durable AI payloads round-trip
correctly by default. At worker startup, `AddDurableAI()` validates the registered client for this;
if you connect a client manually instead (`TemporalClient.ConnectAsync`), you must configure a
`DataConverter` that preserves these contracts — normally `DurableAIDataConverter.Instance`, or a
converter composed on top of its `PayloadConverter` — or the validator fails your worker before
work begins.

`AddDurableTool` registers one tool at a time and takes an optional per-tool configuration callback
(as used above for `weatherTool`, to override the retry/timeout defaults for just that tool).
`AddDurableTools` registers several tools in one call, all under the worker-level defaults, with no
per-tool override:

```csharp
var forecastTool = AIFunctionFactory.Create(
    (string city, int days) => weather.GetForecast(city, days),
    name: "get_forecast",
    description: "Gets a multi-day forecast for a city.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options => { /* ... */ })
    .AddDurableTools(weatherTool, forecastTool);
```

Both contribute to the same implicit worker-owned default toolset. The stock client request stays
thin: it carries no implementation or schema. On a new session, the workflow schedules
`ResolveDurableToolsets` once before `GetChatStep`; Temporal records the returned versioned manifest
and Continue-as-New carries it unchanged. Changed defaults affect new sessions; workers must retain
compatible activation keys and function schemas for recorded in-flight members.

Send a turn without `ChatOptions.Tools`:

```csharp
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);

Console.WriteLine(response.Text);
```

The converter is a worker/client setting, not an AI-workflow setting. If ordinary workflows share
this worker, their callers must use the same compatible converter too. See
[the shared-worker guidance](../../docs/how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows).

## Documentation and samples

- [Durable approvals](../../docs/concepts/durable-approvals.md) — generic per-request approval lifecycle and retry outcomes
- [Worker-owned durable toolsets](../../docs/architecture/MEAI/durable-toolsets.md)
- [Observability](../../docs/how-to/MEAI/observability.md)
- [MEAI usage](../../docs/how-to/MEAI/usage.md)
- [Durable tool contract](../../docs/how-to/MEAI/tool-functions.md)
- [Managed-session tool rules](../../docs/how-to/MEAI/managed-session-tool-rules.md)
- [Pipeline architecture](../../docs/architecture/MEAI/durable-chat-pipeline.md)
- [Sample Catalog](../../samples/catalog.md#temporalcommunityextensionsai-meai) — choose a sample by intent and see current canary coverage

## What the managed session does

`DurableChatSessionClient` maps a `conversationId` to a Temporal workflow. Each `SendAsync` call
is a workflow update. The workflow stores the conversation history, serializes turns, and uses
continue-as-new when its history reaches the configured limit.

When tools are registered, the workflow owns the complete model/tool loop:

```
SendAsync -> DurableChatWorkflow -> GetChatStep activity
                                  -> InvokeFunction activity (per model tool call)
                                  -> GetChatStep activity
                                  -> final response
```

## Supported capabilities

- Durable multi-turn chat sessions via `SendAsync`/`GetHistoryAsync` — [MEAI usage](../../docs/how-to/MEAI/usage.md#send-a-turn-and-read-history)
- Worker-owned durable tools, each call a Temporal activity — [durable tool contract](../../docs/how-to/MEAI/tool-functions.md), [toolset architecture](../../docs/architecture/MEAI/durable-toolsets.md)
- Pre-tool interception (block, skip, approval) — [managed-session tool rules](../../docs/how-to/MEAI/managed-session-tool-rules.md)
- Human approval APIs (per-request; reusable scopes are MAF-only) — [durable approvals](../../docs/concepts/durable-approvals.md), [HITL patterns](../../docs/how-to/MEAI/hitl-patterns.md)
- Keyed MEAI client resolution for multi-model routing — [MEAI usage](../../docs/how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows)
- Durable tool calls from a hand-written custom-workflow Activity (`AIFunction.AsDurable()`) — [pipeline architecture](../../docs/architecture/MEAI/durable-chat-pipeline.md)
- Ordinary MCP client tools via the same `AIFunction` surface — [MCP guide](../../docs/how-to/MEAI/mcp-tools.md)
- Opt-in, bounded payload compression — [payload codec guide](../../docs/how-to/MEAI/payload-codecs.md)

The managed session and a from-scratch custom workflow are separate APIs. In particular, custom
workflow use does not change the durable-session tool contract below.

## Managed-session tool rules

Worker tool registrations are the source of both model-visible schemas and worker implementations.
Use `AddDurableTool`/`AddDurableTools` for one implicit default group or `AddDurableToolset` for
named groups. Two rules are enforced, not just recommended:

```csharp
// 1. Don't pass ChatOptions.Tools to a managed session — the client rejects it.
var options = new ChatOptions { Tools = [weatherTool] };
await sessionClient.SendAsync("customer-42", messages, options); // throws DurableConfigurationException

// 2. Don't add UseFunctionInvocation() to the IChatClient a managed session uses —
// the workflow, not the pipeline, owns the model/tool loop.
builder.Services.AddChatClient(innerChatClient).UseFunctionInvocation(); // wrong for managed sessions
```

Every worker that serves the session task queue must register compatible tool names and schemas.
For side-effecting functions, design for activity retries or opt out with `tool.NoRetry()`. The
per-turn `WithMaxRetryAttempts(...)` override only accepts a positive value; zero and negative
values are rejected so a request cannot opt into Temporal's unbounded-retry semantics.

This package defines the current managed-session contract. It does not offer a compatibility mode
for inline function invocation or caller-supplied session tools.

## Per-call tags and client middleware

Provide activity-span tag metadata from either a managed session or a hand-written custom-workflow Activity:

```csharp
var options = new ChatOptions()
    .WithChatClientTag("tenant", "customer-42")
    .WithChatClientTag("request_id", requestId);

await sessionClient.SendAsync("customer-42", messages, options);
```

Serializable Temporal tag values cross the workflow/activity boundary and are applied directly to
the current model-activity span. Immediately before the provider call, the library removes all
Temporal-private keys while retaining ordinary MEAI options and user-owned `AdditionalProperties`.
Use ordinary MEAI `IChatClient` middleware for retry, routing, logging, telemetry, caching, and
shadowing; `WithChatClientKey(...)` selects among complete keyed client pipelines per call.

Object-typed user properties preserve their JSON content across the default durable converter, but
may arrive at the activity as `JsonElement`; their original CLR runtime type is not guaranteed.
`RawRepresentationFactory` and `ContinuationToken` intentionally do not cross the durable boundary.

## Custom workflow, no session machinery

Most applications that own a custom workflow and want durable LLM calls should reach for
[custom workflow output](../../docs/how-to/MEAI/custom-workflow-output.md)
(`DurableChatWorkflowBase<TOutput>` / `DurableToolWorkflowBase<TRequestData, TTurnState>`) first —
it's backed by the runnable `samples/MEAI/CustomWorkflow` sample and inherits history, HITL, and
continue-as-new for free.

If you want none of that session machinery, the recommended shape is a small hand-written Activity
(constructor-injected `IChatClient`, one `[Activity]` method, standard `Workflow.ExecuteActivityAsync`
dispatch — no durable-adapter ceremony) for the LLM call, plus `AIFunction.AsDurable()` for a
standalone durable tool call from that same workflow. `AsDurable()` is the one low-level primitive
still recommended here: it's a single terminal extension method (`function.AsDurable(options)`),
not a composable middleware pipeline, so it carries none of the determinism hazards that come with
stacking arbitrary chat-client middleware inside workflow code.

The full pattern (Activity + `AsDurable()` combined) is backed by the runnable
`samples/MEAI/DirectAdapters` sample; `samples/MEAI/CustomWorkflow`'s `ShoppingActivities` shows
the same Activity shape for the LLM call. `AsDurable()`'s activity always runs on the calling
workflow's own task queue, regardless of `DurableExecutionOptions.TaskQueue` (which routes the
managed session and other durable chat/embedding activities instead) — see the
[pipeline architecture](../../docs/architecture/MEAI/durable-chat-pipeline.md#direct-adapter-task-queue-boundary)
for the full routing model. For why a hand-written Activity is recommended here instead of
constructing a durable chat/embedding adapter directly in workflow code, see the
[direct-adapter anti-pattern record](../../docs/architecture/MEAI/direct-adapter-anti-pattern.md).

## License

[MIT](../../LICENSE)
