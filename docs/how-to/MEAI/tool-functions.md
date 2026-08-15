# Durable tools with Microsoft.Extensions.AI

`TemporalCommunity.Extensions.AI` supports three registration levels for managed durable tools:

- `AddDurableTools` is the concise choice for one implicit default toolset.
- `AddDurableToolset` creates a named, ordered capability group. Use it when a worker needs to
  combine several groups or a custom workflow must narrow its baseline.
- `AddDurableToolFactory` separates a stable declaration from an invocation-scoped implementation
  for tools that need typed request data, turn state, or scoped DI.

The session workflow supplies frozen function schemas to the model, and each model-requested
invocation is a Temporal activity.

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

The equivalent named grouping is:

```csharp
worker.AddDurableToolset("weather", tools => tools
    .Add(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30))));
```

The stock workflow can combine several named defaults in one ordered baseline:

```csharp
var worker = services.AddHostedTemporalWorker("durable-chat").AddDurableAI(options =>
    options.DefaultToolsetIds = ["catalog", "orders"]);

worker.AddDurableToolset("catalog", tools => tools.Add(searchCatalog));
worker.AddDurableToolset("orders", tools => tools.Add(checkOrder));
```

`null` selects the single implicit toolset registered through `AddDurableTools`; an empty list
creates a deliberate no-tools baseline. Do not combine explicit defaults with `AddDurableTools`.

Toolset IDs and model-visible function names use exact ordinal comparison. Named toolsets cannot
be empty, and members retain their registration order. `weather` and `Weather` are different IDs;
the library does not normalize names or silently resolve collisions.

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

## Upgrading live 0.10.4 tool sessions

Do not carry a live managed tool session started by version 0.10.4 across this upgrade. New
sessions freeze their model-facing tool declarations into the workflow start input; a 0.10.4
workflow history does not contain that field. After an upgraded worker replays such a session,
replay can succeed, but a later turn cannot offer the registered tools to the model.

Before deploying the upgraded worker, stop new turns on those sessions and let them expire or call
`DurableChatSessionClient.ShutdownAsync(conversationId)`. Start replacement sessions after the
upgrade. Replay compatibility for recorded commands does not imply forward-operation compatibility
for new turns on an older live session.

## Invocation-scoped tools for typed turns

For a regular instance method with constructor-injected services, register the handler type and one
explicit method. The library creates the MEAI declaration and receiver activator once, then MEAI
creates and disposes one receiver for each activity attempt:

```csharp
worker.AddDurableToolFactory<InventoryTools>(
    nameof(InventoryTools.LookupAsync),
    new AIFunctionFactoryOptions
    {
        Name = "lookup_inventory",
        Description = "Looks up current inventory.",
    });

sealed class InventoryTools(IInventoryStore store)
{
    public Task<InventoryItem> LookupAsync(
        string sku,
        CancellationToken cancellationToken) =>
        store.LookupAsync(sku, cancellationToken);
}
```

This is the normal DI-backed path. It does not scan the handler class; overloaded methods use the
`MethodInfo` overload. `IServiceProvider`, `AIFunctionArguments`, and `CancellationToken` parameters
follow normal MEAI binding and are excluded from the model schema.

Use the declaration-plus-activation overload only when a tool genuinely needs the `RequestData` or
current `TurnState` supplied to a `DurableToolWorkflowBase` Update, custom MEAI decorators, or a
state-completion callback. The declaration is frozen before workflow start; the implementation is
created inside each tool activity attempt:

```csharp
var declaration = AIFunctionFactory.Create(
    (string itemId) => string.Empty,
    name: "process_item",
    description: "Processes one item.").AsDeclarationOnly();

worker.AddDurableToolFactory<ApplicationRequest, ApplicationTurnState>(
    declaration,
    (services, context) =>
    {
        var processor = services.GetRequiredService<ScopedItemProcessor>();
        return new DurableToolActivation<ApplicationTurnState>
        {
            Function = AIFunctionFactory.Create(
                (string itemId) => processor.ProcessItem(
                    context.RequestData,
                    context.TurnState,
                    itemId),
                name: "process_item"),
        };
    });
```

`ProcessItem` remains a regular .NET method. Only `itemId` appears in the model schema. The factory
runs in the tool activity, never in workflow or model code, and may return an ordinary function
wrapped by existing MEAI `DelegatingAIFunction` decorators. The supplied `IServiceProvider` belongs
to that activity attempt. Resolve scoped services inside the factory; a retry receives a fresh
scope, and disposable scoped services are disposed when the attempt ends. Do not capture that
provider or its scoped services in singleton state.

The implementation name, parameter schema, and return schema must structurally match the frozen
declaration. A mismatch fails non-retryably before invocation. Object-property ordering is ignored,
but array order and scalar values are significant.

Declaration and implementation `AdditionalProperties` must be empty in this version. The values
are arbitrary `object?` instances; silently dropping them could change provider behavior, while
JSON-normalizing them could change their CLR types. Registration reports the tool and sorted keys.
If application behavior depends on those properties, this path is not supported yet.

For a split worker-owned deployment, the workflow-starting process needs no tool registrations:

```csharp
services.AddDurableChatWorkflowInputFactory(taskQueue);
```

The worker registers the named toolsets and the custom workflow selects its maximum baseline via
`DurableToolsetBaselineIds`. The created start input contains session settings but no schemas or
implementations; the workflow resolves its worker-owned manifest once.

The older caller-owned declaration mode remains an advanced control-plane option. Use it only when
the starting process deliberately owns and freezes the schemas before workflow start:

```csharp
services
    .AddDurableChatWorkflowInputFactory(taskQueue)
    .AddDurableToolDeclaration(declaration, configure: ...);
```

`AddDurableChatWorkflowInputFactory` configures clients created through `AddTemporalClient` to use
`DurableAIDataConverter.Instance` when they still use the SDK default converter. A custom converter
is preserved. A client constructed directly with `TemporalClient.ConnectAsync` bypasses DI options
and must set the durable converter explicitly.

Resolve `IDurableChatWorkflowInputFactory` outside workflow code and use its result as the workflow
start input. Call `AddDurableToolImplementation<TRequestData, TTurnState>` in the worker. That
implementation-bearing worker hosts both model and tool activities on the session queue. The
frozen workflow input, not the live worker registry, is the model-facing declaration authority.

An implementation-only worker may use
`AddHostedTemporalWorker(address, namespace, taskQueue)` and set
`RegisterDefaultWorkflow = false` without registering a second `ITemporalClient` in DI. The hosted
worker owns the connection used to poll and complete activities. A process that enables the stock
workflow/session client still needs `AddTemporalClient` because that client starts workflows.

The invocation context includes request data, current turn state, dispatch mode, and activity-local
metadata: namespace, workflow/run/activity identities, attempt, task queue, tool/call identities,
zero-based model iteration and call index, conversation/correlation metadata, and the activity-
scoped idempotency key. It deliberately contains neither the SDK Update ID nor approval-wait
duration.

Request data and turn state are application-supplied lookup inputs, not authenticated claims,
approval evidence, or authorization grants. A high-risk tool must obtain the current authorization
decision from an authoritative service inside the activity immediately before every external
effect.

Use MEAI's existing `DelegatingAIFunction` when an invocation-scoped implementation needs
activity-local validation, authorization, telemetry, or lifecycle observation. Override
`InvokeCoreAsync`, run before logic, delegate to `base.InvokeCoreAsync`, observe success or error,
and use `finally` for attempt-local cleanup. Rethrow failures and cancellation; swallowing them
would prevent Temporal from applying the configured activity retry policy. The decorator and its
dependencies are created from a fresh activity DI scope on every attempt. Ordinary functions need
no special signature and remain the default registration path.

### Activity idempotency key

`Metadata.IdempotencyKey` identifies one scheduled tool activity across its activity retries. The
v1 algorithm is fixed:

1. Write the strict UTF-8 bytes of
   `TemporalCommunity.Extensions.AI/IdempotencyKey/v1\0`.
2. In order, encode namespace, workflow ID, workflow run ID, and activity ID. For each component,
   write its strict UTF-8 byte length as unsigned 32-bit big-endian, then its bytes. Do not trim,
   normalize, or case-fold.
3. SHA-256 hash the complete preimage and return `tai-v1:` plus the lowercase 64-character hex
   digest.

The fixed vector `default`, `workflow-123`,
`01234567-89ab-cdef-0123-456789abcdef`, `7` produces
`tai-v1:4fd719d1966cbf5585d884d8c0dd3c791d9c0737decebd0caa765ad467d36139`.
The activity input carries the algorithm version; unknown or missing versions fail non-retryably
before the factory or function runs. A future algorithm requires a new carried version and output
prefix while workers retain old calculators for supported in-flight activities.

Attempt is diagnostic only and is not part of the key. The key is stable for retries of one
scheduled activity and ordinary replay. It is not promised across Continue-as-New, workflow reset,
a different run, or an application-created replacement operation. Activities—including `NoRetry()`
activities—do not provide exactly-once external effects. Supply this key to downstream idempotency
storage for activity-retry protection, and carry a separate application business-operation ID in
`RequestData` when an effect must deduplicate across workflow runs.

The SDK workflow Update ID is a third, separate identifier: it deduplicates an ambiguous Update
submission only within one workflow run. A cross-run business key can prevent a duplicate external
effect, but it does not suppress the new model/tool loop or guarantee the same `ChatResponse` or
`FinalTurnState`.

## Custom workflow tools

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is not a replacement for the managed chat loop, and it does not make
`ChatOptions.Tools` durable. See the `samples/MEAI/DurableTools` sample for that direct activity
dispatch use case. The function activity runs on the calling workflow's task queue. Even though
`AsDurable()` accepts the shared `DurableExecutionOptions` type, its `TaskQueue` property applies
only to managed sessions and direct chat/embedding adapters and does not reroute the function.

For the complete managed-session boundary, see the
[managed-session tool contract](managed-session-tool-contract.md).

For the worker-owned authority and manifest design, see
[Durable toolsets](../../architecture/MEAI/durable-toolsets.md).
