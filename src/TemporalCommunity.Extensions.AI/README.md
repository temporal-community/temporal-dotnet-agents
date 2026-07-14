# TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` adds Temporal durable execution to
[`Microsoft.Extensions.AI`](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). The
package references MEAI and the Temporal .NET SDK; it does not reference Microsoft Agent Framework.

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
- Registered durable tools through `AddDurableTools`; every model-requested function invocation is
  a Temporal activity with independently configurable timeout and retry behavior.
- Pre-tool decisions through `IDurableToolInterceptor<DurableToolContext>`, including block,
  skip, and approval outcomes.
- Human approval APIs: `GetPendingApprovalAsync`, retry-safe `ResolveApprovalAsync`, and `ShutdownAsync`. Approvals are per-request; reusable approval scopes are an MAF-only capability.
  Approval requests carry an expiration and interceptor-authored reviewer-safe context; use
  `WithApprovalTimeout(...)` when a tool needs a deadline different from the session default.
- Keyed MEAI client resolution through `DefaultChatClientKey` or a per-turn
  `WithChatClientKey(...)` override.
- Direct custom-workflow adapters: `UseDurableExecution()` for `IChatClient` and embedding
  generators, plus `AIFunction.AsDurable()` for an explicitly invoked known function.

The managed session and the direct adapters are separate APIs. In particular, direct custom
workflow use does not change the durable-session tool contract below.

## Managed-session tool contract

`AddDurableTools` is the sole source of both the model-visible function schema and the worker-side
function implementation. Do not put functions in `ChatOptions.Tools` when calling
`DurableChatSessionClient.SendAsync`; the client rejects that configuration. Do not add
`UseFunctionInvocation()` to the `IChatClient` pipeline used by a managed durable session.

Every worker that serves the session task queue must register compatible tool names and schemas.
For side-effecting functions, design for activity retries or use `NoRetry()`.

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
    .AddDurableTools(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
```

Send a turn without `ChatOptions.Tools`:

```csharp
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);

Console.WriteLine(response.Text);
```

## Direct custom-workflow adapters

For a custom workflow that calls a chat client directly, add the chat middleware and set its task
queue. Inside a workflow it schedules one LLM activity; outside a workflow it passes through.

```csharp
builder.Services
    .AddChatClient(innerChatClient)
    .UseDurableExecution(options => options.TaskQueue = "durable-chat")
    .Build();
```

`AIFunction.AsDurable()` is likewise for a custom workflow that explicitly invokes a known
function. The activity worker must register that function with `AddDurableTools`.

The session client has no streaming API. `DurableChatClient.GetStreamingResponseAsync`, when used
directly inside a workflow, buffers one activity result and emits synthetic updates rather than
token-by-token streaming.

## Target framework support

The package ships `net10.0` and `netstandard2.1` assets. Repository samples target `net10.0`.

This package defines the current managed-session contract. It does not offer a compatibility mode
for inline function invocation or caller-supplied session tools.

## Documentation and samples

- [Durable approvals](../../docs/concepts/durable-approvals.md) — generic per-request approval lifecycle and retry outcomes

- [MEAI usage](../../docs/how-to/MEAI/usage.md)
- [Durable tool contract](../../docs/how-to/MEAI/tool-functions.md)
- [Managed-session tool contract](../../docs/how-to/MEAI/managed-session-tool-contract.md)
- [Pipeline architecture](../../docs/architecture/MEAI/durable-chat-pipeline.md)
- [DurableChat sample](../../samples/MEAI/DurableChat)
- [DurableTools sample](../../samples/MEAI/DurableTools)

## License

[MIT](../../LICENSE)
