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

## Invocation-scoped tools for typed turns

Keep ordinary functions as the default. Use an invocation-scoped factory only when a tool genuinely
needs the `RequestData` or current `TurnState` supplied to a `DurableToolWorkflowBase` Update.
The declaration is frozen before workflow start; the implementation is created inside each tool
activity attempt:

```csharp
var declaration = AIFunctionFactory.Create(
    (string itemId) => string.Empty,
    name: "process_item",
    description: "Processes one item.").AsDeclarationOnly();

worker.AddDurableTool<ApplicationRequest, ApplicationTurnState>(
    declaration,
    context => new DurableToolActivation<ApplicationTurnState>
    {
        Function = AIFunctionFactory.Create(
            (string itemId) => ProcessItem(
                context.RequestData,
                context.TurnState,
                itemId),
            name: "process_item"),
    });
```

`ProcessItem` remains a regular .NET method. Only `itemId` appears in the model schema. The factory
runs in the tool activity, never in workflow or model code, and may return an ordinary function
wrapped by existing MEAI `DelegatingAIFunction` decorators.

The implementation name, parameter schema, and return schema must structurally match the frozen
declaration. A mismatch fails non-retryably before invocation. Object-property ordering is ignored,
but array order and scalar values are significant.

Declaration and implementation `AdditionalProperties` must be empty in this version. The values
are arbitrary `object?` instances; silently dropping them could change provider behavior, while
JSON-normalizing them could change their CLR types. Registration reports the tool and sorted keys.
If application behavior depends on those properties, this path is not supported yet.

For split processes, call `AddDurableToolDeclaration` in the workflow-starting process and
`AddDurableToolImplementation<TRequestData, TTurnState>` in the worker. The worker hosting the
session queue must host both model and tool activities. The frozen workflow input, not the live
worker registry, is the model-facing declaration authority.

The invocation context includes request data, current turn state, dispatch mode, and activity-local
metadata: namespace, workflow/run/activity identities, attempt, task queue, tool/call identities,
zero-based model iteration and call index, conversation/correlation metadata, and the activity-
scoped idempotency key. It deliberately contains neither the SDK Update ID nor approval-wait
duration.

Request data and turn state are application-supplied lookup inputs, not authenticated claims,
approval evidence, or authorization grants. A high-risk tool must obtain the current authorization
decision from an authoritative service inside the activity immediately before every external
effect.

## Custom workflow tools

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is not a replacement for the managed chat loop, and it does not make
`ChatOptions.Tools` durable. See the `samples/MEAI/DurableTools` sample for that direct activity
dispatch use case. The function activity runs on the calling workflow's task queue. Even though
`AsDurable()` accepts the shared `DurableExecutionOptions` type, its `TaskQueue` property applies
only to managed sessions and direct chat/embedding adapters and does not reroute the function.

For the complete managed-session boundary, see the
[managed-session tool contract](managed-session-tool-contract.md).
