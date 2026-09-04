# TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` adds Temporal durable execution to
[`Microsoft.Extensions.AI`](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). The
package references MEAI and the Temporal .NET SDK; it does not reference Microsoft Agent Framework.

## Prerequisites

- Temporal Service 1.31.0 or newer.
- .NET 10 SDK or later to build the repository and run its samples. The published package also
  ships a `netstandard2.1` asset.

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

- Durable multi-turn chat sessions with `SendAsync` and `GetHistoryAsync`.
- Worker-owned durable tools through `AddDurableTool`/`AddDurableTools` or named
  `AddDurableToolset` groups; every
  enabled model-requested invocation is a Temporal activity with independently configurable
  timeout and retry behavior.
- Pre-tool decisions through `IDurableToolInterceptor<DurableToolContext>`, including block,
  skip, and approval outcomes.
- Human approval APIs: `GetPendingApprovalAsync`, retry-safe `ResolveApprovalAsync`, and `ShutdownAsync`. Approvals are per-request; reusable approval scopes are an MAF-only capability.
  Approval requests carry an expiration and interceptor-authored reviewer-safe context; use
  `WithApprovalTimeout(...)` when a tool needs a deadline different from the session default.
- Keyed MEAI client resolution through `DefaultChatClientKey` or a per-turn
  `WithChatClientKey(...)` override.
- Direct custom-workflow adapters: `UseDurableExecution()` for `IChatClient` and embedding
  generators, plus `AIFunction.AsDurable()` for an explicitly invoked known function.
- Ordinary MCP client tools through the same `AIFunction` registration surface; see the
  [MCP guide](../../docs/how-to/MEAI/mcp-tools.md).
- Opt-in, bounded payload compression through `DurableAIDataConverter.CreateDataConverter(...)`;
  see the [payload codec guide](../../docs/how-to/MEAI/payload-codecs.md).

The managed session and the direct adapters are separate APIs. In particular, direct custom
workflow use does not change the durable-session tool contract below.

## Managed-session tool contract

Worker tool registrations are the source of both model-visible schemas and worker implementations.
Use `AddDurableTool`/`AddDurableTools` for one implicit default group or `AddDurableToolset` for named groups. Do not
put functions in `ChatOptions.Tools` when calling
`DurableChatSessionClient.SendAsync`; the client rejects that configuration. Do not add
`UseFunctionInvocation()` to the `IChatClient` pipeline used by a managed durable session.

Every worker that serves the session task queue must register compatible tool names and schemas.
For side-effecting functions, design for activity retries or use `NoRetry()`.
When using the per-turn `WithMaxRetryAttempts(...)` override, pass a positive value; zero and
negative values are rejected so a request cannot opt into Temporal's unbounded-retry semantics.

### Upgrade boundary for live 0.10.4 tool sessions

Drain managed tool sessions started by version 0.10.4 before deploying this version. Those
histories contain neither the caller-owned declaration snapshot introduced in 0.12.0 nor the
worker-owned manifest-resolution command introduced by the toolset design. Stop new turns and let
the old sessions expire, or close them with `ShutdownAsync`, then start replacement sessions after
deployment.

## Quick start

Connect a Temporal client with the MEAI-aware data converter and register the worker:

```csharp
var temporalClient = await TemporalClient.ConnectAsync(
    new TemporalClientConnectOptions("localhost:7233")
    {
        DataConverter = DurableAIDataConverter.Instance,
    });

builder.Services.AddSingleton<ITemporalClient>(temporalClient);
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

`AddDurableTool` and `AddDurableTools` contribute to one implicit worker-owned default toolset. The stock client request stays
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

## Per-call tags and client middleware

Provide activity-span tag metadata from either a managed session or the direct workflow adapter:

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

## Direct custom-workflow adapters

For a custom workflow that calls a chat client directly, build the middleware in workflow code and
set its task queue. Inside a workflow it safely schedules one LLM activity and resumes on Temporal's
workflow task scheduler; outside a workflow the same middleware passes through to its inner client.

```csharp
[Workflow]
public sealed class SummaryWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(IReadOnlyList<ChatMessage> messages)
    {
        // WorkflowOnlyChatClient is a sentinel that throws if called. The durable
        // middleware dispatches the request to the worker-side IChatClient instead.
        var chatClient = new ChatClientBuilder(new WorkflowOnlyChatClient())
            .UseDurableExecution(options => options.TaskQueue = "durable-chat")
            .Build();

        var response = await chatClient.GetResponseAsync(messages);
        return response.Text ?? string.Empty;
    }
}
```

Register the real provider-side `IChatClient`, `AddDurableAI()`, and `SummaryWorkflow` on the
worker. Workflow classes do not receive application DI services, and the workflow-local sentinel
must never perform provider I/O.

`DurableExecutionOptions.TaskQueue` is the activity destination for direct chat and embedding
adapters, not an instruction to move the current workflow. A deployment may therefore register
`SummaryWorkflow` on `summary-workflows` and register `AddDurableAI()` activities on
`durable-ai-activities`; setting the adapter task queue to `durable-ai-activities` routes the model
call to the provider worker. Sharing one queue remains supported.

`AIFunction.AsDurable()` is likewise for a custom workflow that explicitly invokes a known
function. Its activity runs on the calling workflow's task queue; the shared
`DurableExecutionOptions.TaskQueue` value does not reroute it. The worker polling that workflow
queue must therefore register the function with `AddDurableTools`.

The session client has no streaming API. `DurableChatClient.GetStreamingResponseAsync` throws when
its async enumerator is advanced inside a workflow; it cannot provide token-by-token streaming
across the workflow/activity boundary. Use `GetResponseAsync` from workflows.

## Target framework support

The package ships `net10.0` and `netstandard2.1` assets. Repository samples target `net10.0`.

This package defines the current managed-session contract. It does not offer a compatibility mode
for inline function invocation or caller-supplied session tools.

## Documentation and samples

- [Durable approvals](../../docs/concepts/durable-approvals.md) — generic per-request approval lifecycle and retry outcomes
- [Worker-owned durable toolsets](../../docs/architecture/MEAI/durable-toolsets.md)
- [Observability](../../docs/how-to/MEAI/observability.md)
- [MEAI usage](../../docs/how-to/MEAI/usage.md)
- [Durable tool contract](../../docs/how-to/MEAI/tool-functions.md)
- [Managed-session tool contract](../../docs/how-to/MEAI/managed-session-tool-contract.md)
- [Pipeline architecture](../../docs/architecture/MEAI/durable-chat-pipeline.md)
- [Sample Catalog](../../samples/catalog.md#temporalcommunityextensionsai-meai) — choose a sample by intent and see current canary coverage

## License

[MIT](../../LICENSE)
