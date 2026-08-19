# Using TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` makes a MEAI `IChatClient` durable with a Temporal session
workflow. A conversation ID becomes a workflow ID; each `SendAsync` is a workflow update; model
calls and registered tool calls execute as activities.

## Worker setup

Both worker and caller must use `DurableAIDataConverter.Instance`. Registration through
`AddTemporalClient` applies it automatically when the converter is still the SDK default. A custom
converter is never replaced. When connecting a Temporal client manually, set it explicitly:

```csharp
var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
});

builder.Services.AddSingleton<ITemporalClient>(client);
builder.Services.AddChatClient(innerClient);

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.ActivityTimeout = TimeSpan.FromMinutes(5);
        options.SessionTimeToLive = TimeSpan.FromHours(24);
        options.MaxToolCallsPerTurn = 10;
    });
```

### Sharing a worker with non-AI workflows

Temporal configures a data converter per client or worker, not per workflow. Calling
`AddDurableAI()` therefore applies `DurableAIDataConverter.Instance` to every workflow and activity
served by that worker's client, including ordinary application workflows registered on the same
worker.

Every client that starts, signals, queries, or reads results from those workflows must use a
compatible converter. This is especially important for a client created directly with
`TemporalClient.ConnectAsync`, because DI option configuration cannot reach it:

```csharp
var options = ClientEnvConfig.LoadClientConnectOptions();
options.DataConverter = DurableAIDataConverter.Instance;
var client = await TemporalClient.ConnectAsync(options);
```

An incompatible client can deserialize a payload into an otherwise valid application object whose
nested members are null or default values; that mismatch need not throw at deserialization time.
The Temporal service does not retain converter identity, so this library cannot diagnose a converter
chosen independently by another process. A custom converter is preserved by the registration APIs,
but it must remain compatible with the MEAI and application payloads used by every participating
client and worker.

For a keyed client, register it with `AddKeyedChatClient` and set
`DurableExecutionOptions.DefaultChatClientKey`.

## Per-call activity tags and client middleware

Attach request-specific values to the durable model-activity span directly:

```csharp
var options = new ChatOptions()
    .WithChatClientTag("tenant", "acme")
    .WithChatClientTag("request_id", requestId);

await sessionClient.SendAsync(conversationId, messages, options);
```

The same `ChatOptions` contract applies to direct `UseDurableExecution()` calls inside custom
workflows. The activity applies the tags immediately before provider invocation and removes their
Temporal-private option keys at the provider boundary.

Use ordinary MEAI `ChatClientBuilder`/`DelegatingChatClient` composition for logging, retry,
routing, telemetry, caching, or shadowing. Register complete keyed `IChatClient` pipelines and use
`WithChatClientKey(...)` when a call must choose among them.

For direct chat and embedding adapters, `DurableExecutionOptions.TaskQueue` is copied to
`ActivityOptions.TaskQueue` on every scheduled model activity. The workflow itself continues on
the queue from its `WorkflowOptions`. This supports a split deployment where workflow workers poll
`my-workflows` and provider/activity workers registered with `AddDurableAI()` poll
`my-ai-activities`. Both queues may be the same, but they are not required to be.

Serializable Temporal routing metadata remains in the durable payload until the activity consumes
it. The provider boundary removes all Temporal-owned keys while retaining ordinary MEAI properties
and user-owned `AdditionalProperties`.

With the default durable converter, arbitrary object-typed user values preserve their JSON content
but may deserialize as `JsonElement`. The library's own routing/tag/timeout/retry getters handle
that converter shape. `RawRepresentationFactory` and `ContinuationToken` are deliberately removed
from durable transport because they cannot be safely resumed.

## Durable tools

Register functions on the worker. `AddDurableTool` and `AddDurableTools` contribute to one implicit
default toolset. The
client-side `SendAsync` request contains no schemas or implementations; the stock workflow resolves
the worker toolset once through `ResolveDurableToolsets`, records the returned manifest, and uses it
for every model/tool iteration and Continue-as-New run.

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI()
    .AddDurableTool(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)))
    .AddDurableTool(writeTool, tool => tool.NoRetry());
```

Do not call `UseFunctionInvocation()` on the session's chat client and do not pass
`ChatOptions.Tools` to `DurableChatSessionClient.SendAsync`. The latter throws
`DurableConfigurationException` by design.

## Send a turn and read history

```csharp
var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);

var history = await sessionClient.GetHistoryAsync("customer-42");
```

Tool calls are recorded as `TemporalCommunity.Extensions.AI.InvokeFunction` activities. Configure
`MaxToolCallsPerTurn` to bound model/tool iteration cost and make side-effecting tools idempotent
or opt them out of retries.

In split deployments, the client process needs `AddDurableChatWorkflowInputFactory` and the
Temporal client but does not register tools. The worker polling the session queue registers
`AddDurableAI` and its default tools. A worker registration change applies only to newly started
sessions; existing sessions retain their recorded manifest and require workers to preserve
compatible activation keys and function schemas for its members.

Streaming is not supported for durable sessions. For a custom workflow that directly invokes a
known function, use `AIFunction.AsDurable()`; it is separate from managed chat-session tool
selection.

See [tool functions](tool-functions.md), [pipeline architecture](../../architecture/MEAI/durable-chat-pipeline.md), and the
[managed-session tool contract](managed-session-tool-contract.md) for the complete contract.
