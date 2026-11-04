# Durable tools with Microsoft.Extensions.AI

`TemporalCommunity.Extensions.AI` supports three registration levels for managed durable tools:

- `AddDurableTools` is the concise choice for one implicit default toolset.
- `AddDurableToolset` creates a named, ordered capability group. Use it when a worker needs to
  combine several groups or a custom workflow must narrow its baseline.
- `AddDurableToolFactory` separates a stable declaration from an invocation-scoped implementation
  for tools that need typed request data, turn state, or scoped DI.

Use `AddDurableToolFactory` for both method-based handlers and explicit declaration/activation
registrations.

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
    .AddDurableTool(
        weatherTool,
        tool => tool.Idempotent().WithTimeout(TimeSpan.FromSeconds(30)));
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

`null` selects the single implicit toolset populated through
`AddDurableTool`/`AddDurableTools`; an empty list creates a deliberate no-tools baseline. Do not
combine explicit defaults with the implicit toolset.

Toolset IDs and model-visible function names use exact ordinal comparison. Named toolsets cannot
be empty, and members retain their registration order. `weather` and `Weather` are different IDs;
the library does not normalize names or silently resolve collisions.

The worker validates stock `DefaultToolsetIds` and cross-selected-toolset function collisions at
startup. A custom workflow's baseline or per-turn narrowing is validated when its recorded
manifest is resolved or narrowed.

Callers then send ordinary chat messages. Do not put functions in `ChatOptions.Tools`.

```csharp
var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "What is the weather in Seattle?")]);
```

The workflow executes this sequence until the model returns a final response:

1. `GetChatStep` calls the configured `IChatClient` with schemas from the workflow's recorded
   worker-owned manifest (or the advanced caller-owned declaration snapshot).
2. Each returned function call becomes an `InvokeFunction` activity.
3. Results are added to the next model step.

The workflow preserves the assistant message contents and correlates tool results by call ID.
MEAI `FunctionCallContent.InformationalOnly` calls and calls already answered in retained
messages are not dispatched again. Duplicate or malformed actionable call IDs reject the whole
batch before any tool activity starts. If a provider reports a tool-call finish but no actionable
calls remain, the turn ends as `IncompleteResponse` instead of repeating model steps indefinitely.
If a dispatched tool activity fails, its result tells the model the external effect outcome is
unknown; an activity failure does not prove that no effect occurred.
Activity outputs are checked against the configured per-payload transport budget before Temporal
records them. If a tool has already completed but its result is too large, the activity returns a
bounded `ResultTooLarge` outcome and the model receives a matching tool error result; the tool is
not invoked again to recover that result. Keep tool results compact or return a reference to
externally stored data. Activity failure messages and details are omitted from transported failures
while retryability and known application failure types are preserved.

## Rules

- `ChatOptions.Tools` is rejected for `DurableChatSessionClient`; it would carry caller-owned
  delegates across a durable boundary.
- Do not install `UseFunctionInvocation()` on the chat-client pipeline used by a durable session.
  The Temporal workflow owns function invocation.
- Register every tool on every worker that can run the session task queue, with stable names.
- Treat side-effecting tools as activities. Managed tool calls default to one activity attempt;
  retries require `tool.Idempotent()` so the downstream system can deduplicate with the logical
  call key. Use `tool.NoRetry()` to explicitly keep one attempt even for an idempotent operation.
  Invocation-scoped factories receive a stable `DurableToolInvocationMetadata.IdempotencyKey`
  derived from the session epoch, request/turn, and distinct tool-call identity. Use it as a
  downstream deduplication key when the external system supports one; it does not provide
  exactly-once effects by itself.
- `MaxToolCallsPerTurn` caps a runaway model/tool loop. Set it for your cost and risk budget.
- Tool approval decisions include the pending request's `CallId` and `FunctionName`; the workflow
  rejects a missing or mismatched call identity before accepting the update. For MEAI approval UIs,
  `DurableApprovalMeaiAdapter` preserves the request ID and function-call identity in both
  directions. Its adapter supports function-call approvals only.

The per-tool callback methods such as `.NoRetry()` configure these managed, worker-registered
tools. They do not configure a separate `AIFunction.AsDurable()` call in custom workflow code;
that wrapper receives its own `DurableExecutionOptions` retry policy.

### Retry safety

Unmarked managed tools default to one activity attempt. Explicit retries require `Idempotent()` and
downstream deduplication using the stable logical-call key:

```csharp
worker.AddDurableTool(readTool, options => options.Idempotent().WithMaxAttempts(3));
worker.AddDurableTool(writeTool, options => options.NoRetry());
```

An explicit multi-attempt policy on a tool without `Idempotent()` is rejected during configuration.
The declaration is an application assertion, not an exactly-once guarantee.

The same payload budget applies to model and tool activity results, not only their inputs. An
oversized model response fails non-retryably because there is no safe model output to continue
with. Oversized tool results are different: the tool effect has completed, so the workflow returns
a bounded error result to the model rather than retrying the tool. A later model/tool call is a new
operation and does not undo or deduplicate the completed effect.

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

The factory's `context.Metadata` includes `SessionEpoch`, `RequestId`, `TurnNumber`,
`ModelIteration`, `ToolCallId`, and `CallIndex`. These are framework-created logical-call context,
not model arguments. The idempotency key remains stable across activity retries and
Continue-as-New, and changes for a new session epoch, request, or distinct tool call. If the model
does not provide a call ID, the deterministic call index is used as the identity fallback.
Applications must still handle uncertain external outcomes; Temporal activity retries and a stable
key do not guarantee that an external effect happens exactly once.

### Logical operation identity

Mark a tool idempotent only when the downstream operation enforces deduplication with the supplied
key. `Idempotent()` records that declaration; retry counts remain controlled by the per-tool
`RetryPolicy` or `WithMaxAttempts` configuration.

```csharp
var declaration = AIFunctionFactory.Create(
    (string recordId) => string.Empty,
    name: "write_record",
    description: "Writes a record.").AsDeclarationOnly();

toolset.AddDurableToolFactory<WriteRequest, WriteState>(
    declaration,
    (services, context) =>
    {
        var sink = services.GetRequiredService<IIdempotentRecordSink>();
        return new DurableToolActivation<WriteState>
        {
            Function = AIFunctionFactory.Create(
                (string recordId) => sink.Write(
                    recordId,
                    context.Metadata.IdempotencyKey),
                name: "write_record"),
        };
    },
    options => options.Idempotent().WithMaxAttempts(3));
```

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
Registering an additional implementation on the worker does not expose it: a model-returned name
absent from the frozen caller declarations receives the same safe blocked result as an unknown
name, before interceptor, approval, factory, or function execution.

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

```csharp
worker.AddDurableToolFactory<ApplicationRequest, ApplicationTurnState>(
    declaration,
    (services, context) =>
    {
        var meta = context.Metadata;
        var logger = services.GetRequiredService<ILogger<ApplicationTools>>();
        logger.LogInformation(
            "tool={Tool} attempt={Attempt} iteration={Iteration} callIndex={CallIndex} mode={Mode}",
            meta.ToolName, meta.Attempt, meta.ModelIteration, meta.CallIndex, context.DispatchMode);

        return new DurableToolActivation<ApplicationTurnState>
        {
            Function = AIFunctionFactory.Create(
                (string itemId) => processor.ProcessItem(context.RequestData, context.TurnState, itemId),
                name: "process_item"),
        };
    });
```

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

```csharp
sealed class TelemetryToolFunction(AIFunction inner, ILogger logger) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Invoking {Tool}", Name);
        try
        {
            // Runs the wrapped function (the ordinary method, or another decorator).
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Tool} failed", Name);
            throw; // Rethrow so the activity retry policy still applies.
        }
        finally
        {
            logger.LogInformation("{Tool} attempt complete", Name);
        }
    }
}
```

Wrap the factory's function with the decorator before returning it: `Function = new
TelemetryToolFunction(AIFunctionFactory.Create(...), logger)`.

### Turn cancellation and tool effects

The managed session exposes `CancelTurnAsync(conversationId, requestId)` to request cancellation
of one active turn. It propagates cancellation to an in-flight model, interceptor, or tool activity
and interrupts a pending approval wait. Temporal waits for activity cancellation to complete
before the turn terminates; activity code and downstream operations must cooperate, so cancellation
may not stop them immediately. No later activity is scheduled. Cancellation does not roll back an
external effect; if an effect may have completed before cancellation was observed, reconcile its
authoritative state and use the logical operation key for downstream idempotency. A caller
cancelling the `SendAsync` RPC wait is not a turn-cancellation request.

Tool activities heartbeat in the background while the function and any turn-state completion
callback are running. This keeps long-running tools healthy under `HeartbeatTimeout` and lets
Temporal deliver cancellation to the `CancellationToken` supplied to the tool and completion
callback. Pass that token to downstream operations; heartbeats do not forcibly interrupt a tool
that ignores it. An `OperationCanceledException` is treated as activity cancellation only when
the activity token is cancelled. A tool's independent timeout is otherwise a bounded, retryable
tool failure, subject to its configured retry policy. An independent timeout in a state-completion
callback remains non-retryable because the tool invocation has already completed.

When a turn terminates after dispatch, retained history includes protocol-valid call/result pairs:
known completed values are kept, uncertain dispatched calls receive an explicit unknown result,
and calls never dispatched receive a not-executed result. The bounded assistant marker omits
partial model prose and raw failure details. A later model turn can use this record, but it does
not establish whether an external effect committed; reconcile through the authoritative system
and use the logical operation key when retrying.

### Activity idempotency key

`Metadata.IdempotencyKey` identifies one logical tool call across activity retries and
Continue-as-New. Read it from the same `DurableToolInvocationContext` supplied to a
declaration-plus-activation factory:

```csharp
(services, context) =>
{
    var idempotencyKey = context.Metadata.IdempotencyKey;
    var store = services.GetRequiredService<IIdempotencyStore>();

    return new DurableToolActivation<ApplicationTurnState>
    {
        Function = AIFunctionFactory.Create(
            async (string orderId) =>
            {
                if (await store.TryGetResultAsync(idempotencyKey) is { } cached)
                {
                    return cached; // A prior attempt for this activity already applied the effect.
                }

                var result = await billing.ChargeAsync(orderId);
                await store.RecordAsync(idempotencyKey, result);
                return result;
            },
            name: "charge_order"),
    };
}
```

Managed session tools use the v2 logical-call algorithm:

1. Write the strict UTF-8 bytes of
   `TemporalCommunity.Extensions.AI/IdempotencyKey/v2\0`.
2. In order, encode namespace, workflow ID, session epoch, request ID, turn number, tool name,
   provider call ID (or `index:{CallIndex}` when absent), model iteration, and call index. For each
   component, write its strict UTF-8 byte length as unsigned 32-bit big-endian, then its bytes. Do
   not trim, normalize, or case-fold.
3. SHA-256 hash the complete preimage and return `tai-v2:` plus the lowercase 64-character hex
   digest.

The workflow run ID, activity ID, and attempt are diagnostic only and are not part of the v2 key.
The session epoch and request/turn identity keep separate sessions and logical turns distinct;
model iteration and call index disambiguate calls within a turn. A direct `AIFunction.AsDurable()`
call has no managed-session request context and continues to use the activity-scoped v1 key.
Unknown or missing versions fail non-retryably before the factory or function runs.

Activities—including `NoRetry()` activities—do not provide exactly-once external effects. Supply
the managed logical-call key to downstream idempotency storage, and retain an application business
operation ID in `RequestData` when effects must deduplicate beyond this session epoch or request.

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
only to managed sessions and direct chat adapters and does not reroute the function.

When `RetryPolicy` is omitted, the activity uses a bounded five-attempt tool default with
exponential backoff capped at 30 seconds. This is a termination backstop, not a total time budget
or exactly-once guarantee. Temporal does not infer HTTP `Retry-After`; translate provider-specific
delay behavior in the activity or configure an explicit policy. Use `MaximumAttempts = 1` for an
effect that is not safe to repeat. Set this policy on the `DurableExecutionOptions` passed directly
to `AsDurable()`; a `.NoRetry()` setting on a separate `AddDurableTool(...)` registration does not
flow to this wrapper. `NoRetry` only prevents Temporal from retrying that activity; it does not
guarantee exactly-once external effects if the activity completed an effect but its completion was
not recorded. An activity already scheduled before a deployment retains the policy recorded in its
`ActivityTaskScheduled` event.

```csharp
[Workflow]
internal sealed class ChargeCardWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(ChargeCardInput input)
    {
        var chargeCard = AIFunctionFactory.Create(
            (string cardToken, decimal amount) => "[stub — not invoked in workflow context]",
            name: "charge_card",
            description: "Charges a payment card. Not safe to retry.")
            .AsDurable(new DurableExecutionOptions
            {
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 }, // non-idempotent effect
            });

        var result = await chargeCard.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["cardToken"] = input.CardToken,
                ["amount"] = input.Amount,
            }));

        return result?.ToString() ?? "No result.";
    }
}
```

For the complete managed-session boundary, see the
[managed-session tool rules](managed-session-tool-rules.md).

For the worker-owned authority and manifest design, see
[Durable toolsets](../../architecture/MEAI/durable-toolsets.md).
