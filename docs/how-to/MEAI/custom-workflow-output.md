# Custom Workflow Output with `DurableChatWorkflowBase<TOutput>`

There are now two intentionally different custom-workflow bases:

| Base | Choose it when | Tool orchestration owner |
|---|---|---|
| `DurableChatWorkflowBase<TOutput>` | Your workflow schedules its own turn activity or orchestration | Your subclass |
| `DurableToolWorkflowBase<TRequestData, TTurnState>` | You want the package's separate model and tool activities with typed per-turn data/state | `TemporalCommunity.Extensions.AI` |

`DurableToolWorkflowBase` is an additive specialization. It does not replace or deprecate
`DurableChatWorkflowBase`, and the existing `CustomWorkflow` sample remains the low-level example.

## Package-managed typed turns

Use `DurableToolWorkflowBase<TRequestData, TTurnState>` when one workflow Update should reuse the
built-in managed tool loop and return a typed result:

```csharp
[Workflow("ApplicationWorkflow")]
public sealed class ApplicationWorkflow
    : DurableToolWorkflowBase<ApplicationRequest, ApplicationTurnState>
{
    protected override IReadOnlyList<string>? DurableToolsetBaselineIds =>
        ["catalog", "operations"];

    [WorkflowRun]
    public new async Task RunAsync(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InitializeInput(input); // synchronously, before the first await
        await base.RunAsync(input).ConfigureAwait(true);
    }

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ApplicationTurnState>> TurnAsync(
        DurableTurnRequest<ApplicationRequest, ApplicationTurnState> request) =>
        RunDurableTurnAsync(request);
}
```

`InitializeInput` must be the first workflow-state operation in a custom run method, before its
first await. Temporal may admit an Update-With-Start handler before the run method executes user
code. An asynchronous handler that needs input may call the protected `WaitForInputAsync`; an Update
validator cannot await and must admit the request when initialization-dependent state is not ready,
leaving authoritative validation to the handler. Validators are an early rejection optimization,
not the only invariant check.

The protected baseline is resolved once and is the workflow's maximum tool authority. A turn may
deterministically narrow it without carrying schemas or implementations in the Update:

```csharp
var request = new DurableTurnRequest<ApplicationRequest, ApplicationTurnState>
{
    Messages = messages,
    RequestData = applicationRequest,
    InitialTurnState = initialState,
    Options = new DurableTurnOptions { ToolsetIds = ["catalog"] },
};
```

`null` uses the complete recorded baseline; an empty list exposes no tools. Caller ordering cannot
reorder the baseline, and an unknown or out-of-baseline ID fails before the model activity. Keep
the protected list fixed and deterministic—workflow code cannot discover live worker DI state.

`RequestData` is immutable application input for one Update. `InitialTurnState` is the state at
that turn's start, and `FinalTurnState` is the last successfully recorded state returned to the
caller. Neither value is inserted into model messages, arguments, or schemas, retained in a
workflow property, or copied to the next Update. If the next turn should start from the previous
result, the application explicitly passes that value as its next `InitialTurnState`.

The specialized base defaults to `Sequential` dispatch. `Parallel` is an explicit optimization
for read-only turn state. The stock `DurableChatWorkflow` remains parallel by default, preserving
its existing command history.

| Mode | Same model-response batch | Turn-state rule |
|---|---|---|
| `Sequential` (default) | All interceptors and approvals resolve first; approved tools then run one at a time in original order | Each successful complete replacement is passed to the next tool |
| `Parallel` (explicit) | All approvals resolve first; approved tools fan out using the existing scheduling shape | Every tool sees batch-start state; an activation with `CompleteState` is rejected before its function runs |

`TTurnState` is both working state for later sequential tools and the structured application output
returned after the turn. Put application-owned actions, receipts, or other completed-turn data in
that state when needed; there is no second application-item collection.

An invocation activation may supply `CompleteState`. It runs only after its ordinary MEAI function
succeeds and returns either `DurableStateUpdate<T>.Unchanged` or `Replace(value)`. `Replace(null)`
is an explicit replacement, not “unchanged.” The callback receives the successfully marshalled
MEAI function result; with MEAI's normal reflected-function marshaller this is commonly a
`JsonElement`, even when the source method returned a .NET scalar. The completion operation must be
side-effect free. Deterministic callback or state-normalization failures are non-retryable so the
ordinary function is not automatically repeated solely because post-processing failed. They also
fail the turn immediately; the workflow does not convert a library-owned configuration or state-
completion failure into a model-visible tool result and ask the model to invoke the tool again.
In sequential mode, the workflow also stops scheduling the remaining calls from that model batch
as soon as it observes such a fatal failure. This prevents later tools from creating new effects
after the turn is already certain to fail. It does not undo the ordinary function or external
effect that completed before its state-completion callback failed; write tools must remain
idempotent and use the invocation metadata's idempotency key.

MEAI chat-client middleware runs around model calls. `DelegatingAIFunction` runs inside an already
scheduled tool activity. The existing `IDurableToolInterceptor` remains the workflow-controlled
pre-dispatch mechanism for proceed, skip, block, argument rewrite, and durable approval. Function
decoration cannot park a workflow for approval because it begins after dispatch. `.RequireApproval()`
is the supported unconditional approval floor; request/state-aware dynamic pre-dispatch approval is
not added by this feature.

Approval is not authorization. Request data, initial or derived turn state, and approval metadata
are not trusted permission evidence. A write tool must reauthorize immediately before every high-
risk external effect using an authoritative service, regardless of whether approval occurred.

`DurableTurnCompletionReason` has three outcomes:

- `FinalResponse` means the model produced a complete final response. This is the only outcome for
  which an application commits `FinalTurnState`.
- `IterationLimitReached` means the package stopped the managed loop at its configured bound.
- `IncompleteResponse` means the provider stopped before a complete response or reported a finish
  reason inconsistent with the response content.

For either non-final outcome, `FinalTurnState` contains only the last successfully recorded state
for diagnostics and is provisional. The application must not commit it. Approval denial/timeout
and recoverable tool failures can become model-visible synthetic results before a later final
response. Workflow cancellation and the configured consecutive-error limit throw instead of
returning a completion reason. A failed model activity uses the consecutive-error allowance but
does not consume a successful model/tool iteration; provider errors classified as permanent fail
the turn immediately.

For newly executed non-final turns, durable conversation history stores only the corresponding
terminal assistant sentinel. Later model calls therefore do not inherit successful tool results
whose application state was discarded. Histories created by 0.12.0 remain replayable through a
Temporal patch marker.

`RunDurableTurnAsync` must be called from a workflow Update and permits one managed turn per Update
ID in the current workflow run. A second call fails the Update before another model/tool dispatch,
including when application code caught a failure from the first call. Each Update handler should
therefore call it exactly once. The SDK Update ID is client retry metadata; it is not copied into
the turn or tool contracts. Continue-as-New starts a new run and resets this guard, so applications
that need cross-run
effect deduplication put a stable business operation identifier in their own `RequestData` and use
it at the downstream system. That can deduplicate an external effect, but it does not prevent a
new model/tool execution or guarantee the same response or final state.

A null request, empty `Messages`, or null `Options` fails the Update promptly with the stable,
non-retryable `DurableTurnInvalidRequest` failure type. These failures schedule no model or tool
activities and do not prevent a later valid Update from running.

Both `RequestData` and turn state are application-supplied history payloads. The library does not
authenticate them, verify freshness, or treat them as authorization evidence. Use payload codecs
when history requires encryption, and reauthorize high-risk effects against an authoritative
service inside the tool activity.

`DurableChatWorkflow` returns a `ChatResponse` from each `[WorkflowUpdate]`. That is the right choice for most applications — it matches what `DurableChatSessionClient` expects and requires no workflow code. But some use cases need something more: a domain-specific type returned atomically from the same Update that drives the LLM turn.

---

## When to Use the Default

`DurableChatWorkflow` with `DurableChatSessionClient` is sufficient when:

- You need multi-turn conversation with history persistence.
- The standard `DurableChatSessionClient.SendAsync` response and persisted history are sufficient.
- You do not need per-turn domain data returned synchronously to the caller.

This is the right starting point. For managed sessions, register worker-owned tools with
`AddDurableTools` or `AddDurableToolset`; the workflow owns the model/tool loop. Most applications
never need a custom workflow.

---

## When to Use `DurableChatWorkflowBase<TOutput>`

Subclass `DurableChatWorkflowBase<TOutput>` when the return value of each Update must carry **domain-specific data** alongside the assistant response, and that data must be returned atomically — not fetched from a separate query or external system after the turn completes.

Concrete examples:

- A shopping assistant returns `CartAction` records produced by tool calls in the same turn.
- A code generation workflow returns the extracted code blocks alongside the assistant explanation.
- A document processing workflow returns structured entities parsed from the LLM output.
- A safety-critical workflow returns a confidence score or structured audit record alongside the response.

If the caller needs this data synchronously — not via a follow-up query — a custom workflow is the right tool.

---

## The Three-Step Pattern

### Step 1: Define Your Output Type

```csharp
public sealed class ShoppingTurnOutput
{
    public required ChatResponse Response { get; init; }
    public IReadOnlyList<CartAction> CartActions { get; init; } = [];
}
```

Your output type can carry anything that is JSON-serializable. It must always include the `ChatResponse` (or its messages) so the base class can append the assistant's messages to the history.

### Step 2: Subclass `DurableChatWorkflowBase<TOutput>`

Implement the three abstract members and add a `[WorkflowUpdate]` method that delegates to `RunTurnAsync`:

```csharp
[Workflow("CustomWorkflow.ShoppingAssistant")]
public sealed class ShoppingAssistantWorkflow : DurableChatWorkflowBase<ShoppingTurnOutput>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdateValidator(nameof(ShopAsync))]
    public void ValidateShop(DurableChatInput input)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input?.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    [WorkflowUpdate("Shop")]
    public async Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input)
    {
        var requestEntry = DurableSessionRequest.FromMessages(input.Messages, input.CorrelationId);
        var (output, _) = await RunTurnAsync(requestEntry, input.Options);
        return output;
    }

    // Wraps the activity output as a DurableSessionResponse for the base class to append
    // to history. The base class calls this once ExecuteTurnAsync completes.
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ShoppingTurnOutput output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);

    protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Flatten the conversation (the request entry is already appended to History) into a
        // single message list for the activity input.
        var allMessages = History.SelectMany(e => e.Messages).ToList();
        var activityInput = new DurableChatInput
        {
            Messages = allMessages,
            Options = chatOptions,
            CorrelationId = requestEntry.CorrelationId,
        };
        return Workflow.ExecuteActivityAsync(
            (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
            activityOptions);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
}
```

Note the `new` keyword on `RunAsync` — it hides the base class's `protected` method to expose a `public` method that Temporal can discover via `[WorkflowRun]`.

### Step 3: Register and Call via Workflow Handle

Register the workflow and its activity class with the worker, then call via the workflow handle rather than `DurableChatSessionClient`:

```csharp
// Worker registration
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "my-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.RegisterDefaultWorkflow = false;  // Skip default workflow; use custom instead
    })
    .AddWorkflow<ShoppingAssistantWorkflow>()
    .AddSingletonActivities<ShoppingActivities>();
```

The `RegisterDefaultWorkflow = false` setting tells `AddDurableAI()` to skip registering `DurableChatWorkflow` and `DurableChatSessionClient` since your custom workflow handles session management instead. All other supporting infrastructure (options, DataConverter, activities, embeddings) is still registered, including `IDurableChatWorkflowInputFactory`.

`RunTurnAsync` passes your `ExecuteTurnAsync` implementation the configured `RetryPolicy`, or the
library's bounded default (five attempts) when none is configured. For a non-idempotent custom
activity, replace it with a stricter policy such as `new RetryPolicy { MaximumAttempts = 1 }`.

```csharp
// Start the workflow
var workflowInput = host.Services
    .GetRequiredService<IDurableChatWorkflowInputFactory>()
    .Create() with
    {
        TimeToLive = TimeSpan.FromHours(1),
    };

var handle = await temporalClient.StartWorkflowAsync(
    (ShoppingAssistantWorkflow wf) => wf.RunAsync(workflowInput),
    new WorkflowOptions(workflowId, taskQueue)
    {
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
    });

// Send a turn and receive the typed output
var output = await handle.ExecuteUpdateAsync<ShoppingTurnOutput>(
    "Shop",
    [new DurableChatInput { Messages = userMessages }]);

Console.WriteLine(output.Response.Messages.Last().Text);
foreach (var action in output.CartActions)
    Console.WriteLine($"[{action.Action}] {action.ProductName}");
```

Resolve the factory in client/host code, never inside a workflow. It snapshots the same retry,
timeout, history-reducer, tool, interceptor, and approval configuration as the stock session
client. In split client and worker deployments, both processes must register matching durable AI
configuration before the client creates the start input; the serialized input, not worker DI,
becomes the replay-frozen authority for that session.

---

## The Three Abstract Members

### `BuildResponseEntry(correlationId, output, createdAt)`

Wraps the per-turn `TOutput` into a `DurableSessionResponse` that the base class appends to history. The correlation ID matches the request entry that was already appended.

```csharp
protected override DurableSessionResponse BuildResponseEntry(
    string correlationId,
    ShoppingTurnOutput output,
    DateTimeOffset createdAt) =>
    DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);
```

For a `ChatResponse`-based output, the static `DurableSessionResponse.FromChatResponse` factory captures the assistant messages, usage, and timestamp. If your output type does not include the full `ChatResponse`, construct a `DurableSessionResponse` directly from the assistant messages you want preserved.

### `ExecuteTurnAsync(ActivityOptions activityOptions, DurableSessionRequest requestEntry, ChatOptions? chatOptions)`

Dispatches the LLM call (or custom logic) as a Temporal activity. The base class supplies pre-built `ActivityOptions` (timeout, heartbeat, summary), the request entry that was just appended to history, and the per-turn `ChatOptions` (model id, tools).

```csharp
protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
    ActivityOptions activityOptions,
    DurableSessionRequest requestEntry,
    ChatOptions? chatOptions)
{
    var allMessages = History.SelectMany(e => e.Messages).ToList();
    var activityInput = new DurableChatInput
    {
        Messages = allMessages,
        Options = chatOptions,
        CorrelationId = requestEntry.CorrelationId,
    };
    return Workflow.ExecuteActivityAsync(
        (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
        activityOptions);
}
```

You can dispatch to any registered activity class — it does not have to be derived from `DurableChatActivities`. The base exposes the full conversation history via the protected `History` property; the activity input is yours to construct.

### `CreateContinueAsNewException(DurableChatWorkflowInput input)`

Creates the `ContinueAsNewException` typed to the concrete workflow class. The base class calls this when `Workflow.ContinueAsNewSuggested` is true, passing the new input with the carried history.

```csharp
protected override ContinueAsNewException CreateContinueAsNewException(
    DurableChatWorkflowInput input) =>
    Workflow.CreateContinueAsNewException(
        (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
```

The concrete type in the lambda must match the actual workflow class — if you use the wrong type, Temporal will start a workflow of the wrong kind on the next run.

The `input` supplied to this hook is a record clone of the workflow's original frozen session
configuration. The base replaces only run-scoped values such as carried history, resolved approval
history, and the original creation time. Retry policies, keyed history reducers, per-tool options,
interceptor settings, and future frozen settings are preserved automatically. Pass this object
through unchanged unless your derived workflow has an explicitly documented run-scoped field to
replace; rebuilding it property-by-property can silently drop configuration.

---

## What You Inherit

By extending `DurableChatWorkflowBase<TOutput>` you get the following at no cost:

- **Session loop** — `RunAsync` waits for shutdown or `ContinueAsNewSuggested`, then transitions or returns.
- **Conversation history** — full `List<DurableSessionEntry>` persisted in workflow state, restored on continue-as-new. Each turn appends a `DurableSessionRequest` followed by a `DurableSessionResponse`.
- **Turn serialization** — `WaitConditionAsync(() => !_isProcessing)` prevents concurrent turns from corrupting history.
- **HITL** — `[WorkflowUpdate("RequestApproval")]`, `[WorkflowUpdate("ResolveApproval")]`, and `[WorkflowQuery("GetPendingApproval")]` are wired to `DurableApprovalMixin` automatically.
- **Continue-as-new** — history is carried forward when workflow history grows large; search attributes and the complete frozen session configuration are preserved.
- **Search attributes** — optional `TurnCount` and `SessionCreatedAt` upserts via `DurableSessionAttributes` when `input.EnableSearchAttributes` is `true`.
- **`[WorkflowQuery("GetHistory")]`** — returns the current conversation history.
- **`[WorkflowSignal("Shutdown")]`** — sets `IsShutdownRequested` and unblocks the session loop.

---

## Comparison

| | Default (`DurableChatWorkflow` + `DurableChatSessionClient`) | Custom (`DurableChatWorkflowBase<TOutput>`) |
|---|---|---|
| Return type from Update | `ChatResponse` | Any serializable type |
| Domain data per turn | Via separate query or side channel | Returned atomically with the response |
| Activity class | `DurableChatActivities` (built-in) | Your own class |
| Entry point | `DurableChatSessionClient.SendAsync` | `WorkflowHandle.ExecuteUpdateAsync` |
| Registration | `AddDurableAI()` only | `AddDurableAI()` + `AddWorkflow<T>()` + `AddSingletonActivities<T>()` |
| Code to write | None | Three abstract method overrides + Update method |
| HITL | Inherited | Inherited |
| Continue-as-new | Inherited | Inherited |
| History persistence | Inherited | Inherited |

---

## Sample Code

The full implementation is in `samples/MEAI/CustomWorkflow/`:

- `ShoppingAssistantWorkflow.cs` — the concrete `DurableChatWorkflowBase<ShoppingTurnOutput>` subclass
- `ShoppingActivities.cs` — the activity class with cart tool definitions and `GetShoppingResponseAsync`
- `ShoppingTurnOutput.cs` — the typed output carrying `ChatResponse` + `IReadOnlyList<CartAction>`
- `CartAction.cs` — the domain type for cart mutations
- `Program.cs` — host setup, workflow start, and two-turn demo
