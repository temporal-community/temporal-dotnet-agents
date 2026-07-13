# Using TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` makes a MEAI `IChatClient` durable with a Temporal session
workflow. A conversation ID becomes a workflow ID; each `SendAsync` is a workflow update; model
calls and registered tool calls execute as activities.

## Worker setup

Both worker and caller must use `DurableAIDataConverter.Instance` when connecting a Temporal
client manually.

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

For a keyed client, register it with `AddKeyedChatClient` and set
`DurableExecutionOptions.DefaultChatClientKey`.

## Durable tools

Register functions on the worker. The registry supplies the schema to the model and the activity
implementation to the worker.

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI()
    .AddDurableTools(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)))
    .AddDurableTools(writeTool, tool => tool.NoRetry());
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

Streaming is not supported for durable sessions. For a custom workflow that directly invokes a
known function, use `AIFunction.AsDurable()`; it is separate from managed chat-session tool
selection.

See [tool functions](tool-functions.md), [pipeline architecture](../../architecture/MEAI/durable-chat-pipeline.md), and the
[migration guide](migration-breaking-changes.md) for the complete contract.
