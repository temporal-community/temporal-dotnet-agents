# Durable tools with Microsoft.Extensions.AI

`TemporalCommunity.Extensions.AI` supports one managed tool model for a durable chat session:
register every callable function with `AddDurableTools`. The session workflow supplies those
function schemas to the model, and each model-requested invocation is a Temporal activity.

```csharp
var weatherTool = AIFunctionFactory.Create(
    (string city) => weather.GetCurrent(city),
    name: "get_weather",
    description: "Gets current weather for a city.");

builder.Services.AddChatClient(innerClient);

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.MaxToolCallsPerTurn = 10;
        options.RetryPolicy = new RetryPolicy { MaximumAttempts = 3 };
    })
    .AddDurableTools(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
```

Callers then send ordinary chat messages. Do not put functions in `ChatOptions.Tools`.

```csharp
var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);
```

The workflow executes this sequence until the model returns a final response:

1. `GetChatStep` calls the configured `IChatClient` with schemas from the durable registry.
2. Each returned function call becomes an `InvokeFunction` activity.
3. Results are added to the next model step.

## Rules

- `ChatOptions.Tools` is rejected for `DurableChatSessionClient`; it would carry caller-owned
  delegates across a durable boundary.
- Do not install `UseFunctionInvocation()` on the chat-client pipeline used by a durable session.
  The Temporal workflow owns function invocation.
- Register every tool on every worker that can run the session task queue, with stable names.
- Treat side-effecting tools as activities: make them idempotent where possible, or use
  `tool.NoRetry()` when a retry would repeat an unsafe operation.
- `MaxToolCallsPerTurn` caps a runaway model/tool loop. Set it for your cost and risk budget.

## Custom workflow tools

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is not a replacement for the managed chat loop, and it does not make
`ChatOptions.Tools` durable. See the `samples/MEAI/DurableTools` sample for that direct activity
dispatch use case.

For the complete managed-session boundary, see the
[managed-session tool contract](managed-session-tool-contract.md).
