# Getting Started with TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` makes any `IChatClient` (Microsoft.Extensions.AI / MEAI) durable using Temporal workflows — no Microsoft Agent Framework required. You keep your existing MEAI pipeline and conversation code; the library wraps each LLM call as a Temporal activity and stores conversation history inside a long-running workflow.

The key insight is the mapping between MEAI concepts and Temporal primitives. Each **conversation** becomes a **Temporal workflow** identified by a `conversationId` string you control. Each **LLM call** (a single `GetResponseAsync` invocation) becomes a **Temporal activity** with configurable timeouts, retry policy, and crash recovery. If the worker process crashes mid-call, Temporal retries the activity automatically from where it left off — the conversation history is safe in workflow state.

This is meaningfully different from a plain MEAI pipeline. A raw `IChatClient` call loses all state if the process crashes; the conversation history lives only in the caller's memory. With this library, history survives process restarts, worker redeploys, and network blips. The workflow's event history is the source of truth, and the `DurableChatSessionClient` is the external entry point for sending messages to it.

---

## Prerequisites

- .NET 10 SDK
- A running Temporal server (`temporal server start-dev` starts one on `localhost:7233`)
- An LLM provider — OpenAI, Azure OpenAI, Ollama, or any provider with an MEAI adapter
- The NuGet package:

```bash
dotnet add package TemporalCommunity.Extensions.AI
```

---

## Step 1 — Connect the Temporal Client

```csharp
// Both: required in the worker process and in any external caller process
var temporalClient = await TemporalClient.ConnectAsync(
    new TemporalClientConnectOptions("localhost:7233")
    {
        DataConverter = DurableAIDataConverter.Instance,
        Namespace = "default",
    });

builder.Services.AddSingleton<ITemporalClient>(temporalClient);
```

> **Note:** `DurableAIDataConverter.Instance` is required whenever you use MEAI types in workflow history. MEAI's `AIContent` hierarchy is polymorphic — `TextContent`, `FunctionCallContent`, `FunctionResultContent`, and others all serialize with a `$type` discriminator field that tells the deserializer which concrete type to construct. Temporal's default `DefaultPayloadConverter` uses plain `System.Text.Json` without that discriminator support, so `FunctionCallContent` and `FunctionResultContent` instances round-trip through workflow history as base `AIContent` objects and lose all their data. `DurableAIDataConverter` wraps the payload converter with `AIJsonUtilities.DefaultOptions`, which includes the correct polymorphic type resolvers.
>
> **Auto-wiring:** When using `AddTemporalClient(addr, ns)` or the 3-arg `AddHostedTemporalWorker(addr, ns, queue)` overload, `AddDurableAI()` applies `DurableAIDataConverter` automatically — you do not need to set it manually. The explicit `DataConverter = DurableAIDataConverter.Instance` shown above is only required when creating the client via `TemporalClient.ConnectAsync` and registering it with `AddSingleton<ITemporalClient>`.

---

## Step 2 — Register IChatClient

Use the idiomatic MEAI DI pattern — `AddChatClient` returns a `ChatClientBuilder` for chaining middleware, and `Build()` registers the final `IChatClient` singleton:

```csharp
// Worker
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()   // handles tool call loops inside the activity
    .Build();
```

`DurableChatActivities` — the internal activity class that executes LLM calls — resolves the `IChatClient` per-invocation using the following priority order:

1. `ChatOptions.WithChatClientKey("key")` — per-call override set by the caller
2. `DurableExecutionOptions.DefaultChatClientKey` — worker-level default key set in `AddDurableAI`
3. Unkeyed `IChatClient` — existing fallback when no key is specified

> **Note:** If you use `AddKeyedChatClient` to manage multiple LLM clients in one application, set `DefaultChatClientKey` in `AddDurableAI` instead of registering a redundant unkeyed alias:
>
> ```csharp
> // Worker
> builder.Services
>     .AddKeyedChatClient("chat", gptClient)
>     .UseFunctionInvocation()
>     .Build();
>
> builder.Services
>     .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
>     .AddDurableAI(opts =>
>     {
>         opts.DefaultChatClientKey = "chat";   // resolves AddKeyedChatClient("chat", ...)
>     });
> ```
>
> To switch to a different client for a single turn without changing the worker configuration, pass a per-call override via `ChatOptions`:
>
> ```csharp
> // Client
> var options = new ChatOptions().WithChatClientKey("routing");
> var response = await sessionClient.ChatAsync(conversationId, messages, options: options);
> ```

---

## Step 3 — Register the Worker

Chain `AddDurableAI` onto the hosted worker builder:

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

`AddDurableAI` registers everything needed on the worker:

| Registered type | Role |
|---|---|
| `DurableChatWorkflow` | The workflow that owns conversation history and dispatches LLM calls |
| `DurableChatActivities` | The activity that calls `IChatClient.GetResponseAsync` |
| `DurableFunctionActivities` | The activity that resolves and invokes durable tool functions by name |
| `DurableEmbeddingActivities` | The activity that calls `IEmbeddingGenerator.GenerateAsync` |
| `DurableChatSessionClient` | The external entry point injected into your application code |

Nothing else needs to be wired up manually. The `TaskQueue` is automatically read from the worker builder and set on `DurableExecutionOptions`.

> **`ITemporalClient` prerequisite:** `AddDurableAI` requires `ITemporalClient` to be registered in DI before it is called. Call `services.AddTemporalClient(address, namespace)` to register it. The 3-arg `AddHostedTemporalWorker(address, namespace, queue)` overload stores connection settings on the worker service but does **not** register `ITemporalClient` as a DI service — `AddTemporalClient` is still required separately.

---

## Step 4 — Send a Message

Resolve `DurableChatSessionClient` from DI and call `ChatAsync`:

```csharp
// Client
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

var conversationId = "user-42-session-1";   // any stable string you control

DurableSessionResponse response = await sessionClient.ChatAsync(
    conversationId,
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);

Console.WriteLine(response.Text);   // "Paris."
```

`ChatAsync` returns `DurableSessionResponse` — the workflow's per-turn response entry. It carries:

- `Messages` — the assistant's reply messages
- `Usage` — `UsageDetails?` with input/output token counts (when the model returned them)
- `CorrelationId` — the per-turn correlation ID (auto-generated unless you supplied one)
- `CreatedAt` — when the workflow recorded the response
- `Text` — convenience accessor returning the last assistant message's text (empty string if none)

`ChatAsync` starts the `DurableChatWorkflow` if it is not already running (using `WorkflowIdConflictPolicy.UseExisting`), then sends the messages via a `[WorkflowUpdate]`. The update blocks until the LLM activity completes and returns the response. If the workflow is already running from a previous turn, the update is routed to the existing instance.

#### Supplying your own correlation ID

Pass an optional `correlationId` argument to thread an upstream HTTP/gRPC trace ID, request ID, or any cross-system identifier through the workflow turn. When omitted (or null/empty), the workflow auto-generates one via `Workflow.NewGuid()`.

```csharp
// Client — thread the inbound HTTP request's trace identifier through the agent turn
var traceId = httpContext.TraceIdentifier;
var response = await sessionClient.ChatAsync(
    conversationId,
    [new ChatMessage(ChatRole.User, "What is the capital of France?")],
    correlationId: traceId);

// The same trace ID is now stamped on the request and response entries in workflow history,
// queryable later via GetHistoryAsync.
Console.WriteLine(response.CorrelationId);   // "0HMVB..." (your trace ID)
```

### Multi-turn conversations

Pass the same `conversationId` on every turn. The workflow accumulates history internally across calls:

```csharp
// Client
var conversationId = "user-42-session-1";

var r1 = await sessionClient.ChatAsync(conversationId,
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);
Console.WriteLine(r1.Text);   // "Paris."

// The workflow already holds the first exchange in its state.
var r2 = await sessionClient.ChatAsync(conversationId,
    [new ChatMessage(ChatRole.User, "What is the population of that city?")]);
Console.WriteLine(r2.Text);   // "Approximately 2.1 million..."
```

Each `ChatAsync` call only needs to send the new message — the workflow maintains the running history and passes the full context to the LLM on every turn.

### Retrieving history

`GetHistoryAsync` sends a Temporal Query to the running workflow and returns every entry accumulated so far. Each turn produces two entries — a `DurableSessionRequest` (the user/tool messages that initiated the turn) and a `DurableSessionResponse` (the assistant's reply, with `Usage` token counts attached). Pattern-match on the entry type to access the per-turn metadata:

```csharp
// Client
IReadOnlyList<DurableSessionEntry> history = await sessionClient.GetHistoryAsync(conversationId);

foreach (var entry in history)
{
    foreach (var msg in entry.Messages)
    {
        var text = string.Concat(msg.Contents.OfType<TextContent>().Select(c => c.Text));
        Console.WriteLine($"[{msg.Role}] {text}");
    }

    if (entry is DurableSessionResponse response)
    {
        // Per-turn token usage is now first-class — no external telemetry needed.
        var inTokens  = response.Usage?.InputTokenCount  ?? 0;
        var outTokens = response.Usage?.OutputTokenCount ?? 0;
        Console.WriteLine(
            $"   turn {response.CorrelationId}: in={inTokens} out={outTokens}");
    }
}
```

To get a flat `ChatMessage` log (the prior return shape) for downstream display code that expects messages rather than entries:

```csharp
var flatMessages = history.SelectMany(e => e.Messages).ToList();
```

---

## Per-Request Overrides

The extension methods on `ChatOptions` let you override the global `DurableExecutionOptions` for a single turn without changing the worker configuration:

```csharp
// Client
var options = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))
    .WithMaxRetryAttempts(5)
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(3));

var response = await sessionClient.ChatAsync(conversationId, messages, options: options);
```

These values are stored in `ChatOptions.AdditionalProperties` under well-known string keys (`temporal.activity.timeout`, `temporal.retry.max_attempts`, `temporal.heartbeat.timeout`). The workflow reads them when scheduling the activity and applies them for that invocation only.

---

## Reducing the LLM Context Window

For long-running sessions the full conversation history can grow large enough to make LLM calls expensive. Chain a plain stateless `IChatReducer` — such as MEAI's `MessageCountingChatReducer` — onto your inner chat client so each turn only sends the LLM a sliding window. The reducer runs inside the Temporal activity, never in workflow context, so it does not need to be replay-safe:

```csharp
// Worker
builder.Services
    .AddChatClient(innerClient)
    .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
    .UseFunctionInvocation()
    .UseDurableExecution()
    .Build();
```

With this configuration:

- The `DurableChatWorkflow` retains every message ever exchanged in the conversation as part of its event history — that is the durable, replay-safe source of truth.
- The reducer passes only the most recent 20 messages to the LLM on each turn.
- `GetHistoryAsync` still returns the full unreduced log straight from workflow state.

To retrieve the complete conversation at any point, query the workflow:

```csharp
// Client — full, durable history straight from the workflow
var history = await sessionClient.GetHistoryAsync("conversation-123");
```

> **Design rationale:** Full conversation history is kept on the workflow itself (`DurableChatWorkflow._history`), where it is replay-safe and durable via Temporal event history. Reducers are stateless and only shape what is sent to the LLM on each turn — they never own conversation state.

> **Note:** `MessageCountingChatReducer` is provided by the MEAI library (`Microsoft.Extensions.AI`). Any `IChatReducer` implementation works here — token-counting reducers, summarization reducers, etc. — as long as it is stateless or scoped per-call.

---

## Tool Functions

Tools passed via `ChatOptions.Tools` are handled by the `UseFunctionInvocation()` middleware in the existing pipeline. The entire tool call loop (LLM request → tool invocation → LLM request with result) runs inside the single Temporal activity — the tool function executes on the worker process.

For tool functions that need their own durability guarantees — individual retry policies, separate timeouts, or independent crash recovery — the library provides two further models:

- **Model 2** — wrap tools with `.AsDurable()` inside a custom `[Workflow]`. Each tool call becomes its own Temporal activity, dispatched by your workflow code.
- **Model 3** — register tools with `AddDurableTools()` and **omit** `UseFunctionInvocation()`. `DurableChatSessionClient` automatically runs a per-tool dispatch loop inside its own workflow — no custom workflow code required.

See [tool-functions.md](tool-functions.md) for all three execution models and guidance on choosing between them.

---

## Per-Tool Activity Options

When using Model 3 (`AddDurableTools` without `UseFunctionInvocation`), each tool call becomes its own Temporal activity. The library exposes three worker-level knobs on `DurableExecutionOptions` and a per-tool builder for fine-grained overrides.

### Worker-level defaults

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.MaxToolCallsPerTurn               = 20;   // cap loop iterations per turn (default 20)
        opts.MaximumConsecutiveErrorsPerRequest = 3;   // tool-failure threshold (default 3)
        opts.IncludeDetailedErrors             = false; // include exception detail in synthesized errors
    });
```

| Option | Type | Default | Purpose |
|---|---|---|---|
| `MaxToolCallsPerTurn` | `int` | 20 | Maximum LLM↔tool iterations within a single turn. When exceeded, the workflow synthesizes an "iteration limit exceeded" assistant message and returns — it does not throw. (See [Pattern 3 Loop Semantics](#pattern-3-loop-semantics) below.) |
| `MaximumConsecutiveErrorsPerRequest` | `int` | 3 | Number of consecutive failed tool steps tolerated before a non-retryable `ApplicationFailureException` surfaces to the caller. Resets to 0 after any all-success step. Set to **`0`** to propagate the first tool failure immediately (MAF-style behavior). (See [Pattern 3 Loop Semantics](#pattern-3-loop-semantics) below.) |
| `IncludeDetailedErrors` | `bool` | `false` | When `true`, synthesized `FunctionResultContent` error messages include the exception type and message (`"Error: {message} ({errorType})"`). When `false`, callers see a generic `"Error: Tool invocation failed."`. |

### Per-tool overrides (`DurableChatToolOptions`)

`AddDurableTools` has a single-tool overload that accepts a configuration callback:

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI()
    .AddDurableTools(weatherTool)                                                // default options
    .AddDurableTools(stockTool, opts => opts.WithTimeout(TimeSpan.FromSeconds(30)))
    .AddDurableTools(sendEmail, opts => opts.NoRetry());                         // non-idempotent write tool
```

`DurableChatToolOptions` mirrors the Agents library's `DurableToolOptions` for cross-library symmetry:

| Member | Type | Notes |
|---|---|---|
| `StartToCloseTimeout` | `TimeSpan?` | Activity start-to-close timeout. Falls back to `DurableExecutionOptions.ActivityTimeout` when null. |
| `HeartbeatTimeout` | `TimeSpan?` | Activity heartbeat timeout. Falls back to `DurableExecutionOptions.HeartbeatTimeout` when null. |
| `RetryPolicy` | `RetryPolicy?` | Full retry policy. Falls back to `DurableExecutionOptions.RetryPolicy` when null. |
| `NoRetry()` | fluent | Sets `RetryPolicy = new RetryPolicy { MaximumAttempts = 1 }`. Use for non-idempotent write tools to prevent double-execution on transient failure. |
| `WithMaxAttempts(int)` | fluent | Sets `MaximumAttempts` on the retry policy. |
| `WithTimeout(TimeSpan)` | fluent | Sets `StartToCloseTimeout`. |

Power users assign `RetryPolicy` directly for custom backoff or non-retryable error types:

```csharp
.AddDurableTools(myTool, opts =>
{
    opts.RetryPolicy = new RetryPolicy
    {
        MaximumAttempts        = 5,
        InitialInterval        = TimeSpan.FromSeconds(1),
        BackoffCoefficient     = 2.0,
        NonRetryableErrorTypes = new[] { typeof(InvalidOperationException).FullName! },
    };
});
```

### Resolution chain

For each registered tool the effective `ActivityOptions` is:

- `StartToCloseTimeout` = per-tool `StartToCloseTimeout` ?? `DurableExecutionOptions.ActivityTimeout`
- `HeartbeatTimeout` = per-tool `HeartbeatTimeout` ?? `DurableExecutionOptions.HeartbeatTimeout`
- `RetryPolicy` = per-tool `RetryPolicy` ?? `DurableExecutionOptions.RetryPolicy`
- `Summary` = tool name (auto-populated for the Temporal Web UI)

The resolved dict is captured into workflow history at session start, so per-tool options are **frozen for the lifetime of a session** — a new `AddDurableTools` registered after a session begins will not affect the in-flight session. See [tool-functions.md — Mid-session drift warning](tool-functions.md#mid-session-drift-warning). The client-side per-tool options map is also cached once at first use — see the Mid-session drift warning in tool-functions.md.

---

## Pattern 3 Loop Semantics

`MaxToolCallsPerTurn` and `MaximumConsecutiveErrorsPerRequest` are **independent counters** with different jobs. Confusing them leads to surprising turn-abort behavior, so it is worth pinning down exactly what each one counts.

`MaxToolCallsPerTurn` bounds the outer `for` loop in the workflow ([`DurableChatWorkflow.cs:167`](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L167)). Every iteration consumes exactly one slot — it does not matter whether the tool calls in that iteration succeeded or faulted. Errors **do** count against this iteration budget.

`MaximumConsecutiveErrorsPerRequest` is a separate counter that only moves when *at least one* tool call in an iteration faulted (`hadError = true` at [line 245](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L245), incremented at [lines 260–270](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L260)). A fully successful iteration resets it back to zero ([line 273](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L273)).

### What happens on each iteration

1. **LLM step.** The workflow dispatches `GetChatStepAsync` ([line 179](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L179)). The LLM call itself is **not gated** by either counter — an LLM failure surfaces directly as an activity exception, and it does **not** increment the error counter.
2. **Tool fan-out.** If the response contains tool calls, they dispatch in parallel via `Workflow.WhenAllAsync` ([line 226](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L226)).
3. **Per-task inspection.** The workflow walks every task individually and synthesizes one `FunctionResultContent` per `CallId` in original order ([lines 235–258](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L235-L258)) — successful results carry the activity output; failures carry an error message that gets fed back to the LLM. Every `CallId` always gets a result; partial-failure turns never leave orphan IDs (OpenAI/Anthropic reject those).
4. **Consecutive-error check.** *After* synthesizing tool-result messages, the workflow checks the threshold ([line 263](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L263)). Exceeding it throws a non-retryable `ApplicationFailureException` that terminates the turn.

### The three behavioral modes

- **`MaximumConsecutiveErrorsPerRequest = 0`** — immediate-failure agent. The first faulted iteration aborts the turn.
- **Default of `3`** — up to three consecutive partially-faulting iterations are tolerated before the turn aborts. Any all-success iteration mixed in resets the counter.
- **Errors effectively disabled (very large value)** — `MaxToolCallsPerTurn` still caps total dispatches. When the cap is hit the workflow synthesizes the sentinel assistant message ([lines 281–293](../../../src/TemporalCommunity.Extensions.AI/DurableChatWorkflow.cs#L281-L293)) and returns cleanly — it does **not** throw.

In other words: iteration-cap exhaustion is a **soft** outcome (sentinel message, healthy workflow), while exceeding the consecutive-error budget is a **hard** outcome (non-retryable failure propagated to the caller). Choose `MaximumConsecutiveErrorsPerRequest` based on how loudly you want a stuck tool to fail.

---

## Pattern 3 Observability

Model 3 turns naturally emit **multiple existing spans per turn** — no new OpenTelemetry surface to learn:

- One `chat {modelId}` span per LLM iteration (inside `GetChatStepAsync`)
- One `execute_tool {toolName}` span per tool invocation (inside `InvokeFunctionAsync`)
- All correlated by the `conversation.id` span attribute and per-turn `correlationId`

Filter by `conversation.id` in your tracing backend to see every span for a single Model 3 turn. The existing search attributes (`TurnCount`, `SessionCreatedAt`) continue to work at the session level — no new attributes are introduced for Model 3.

The workflow itself emits no diagnostic spans. Spans inside `[Workflow]` code are non-deterministic on replay, so all Model 3 telemetry lives on the activity side.

---

## Long-Running Turns

Temporal's update RPC has a default **~60-second** timeout. A Model 3 turn that iterates through several tool calls can legitimately exceed this — the workflow is healthy, the LLM just needs more wall-clock time to converge.

The canonical recovery pattern uses `WorkflowUpdateStage.Accepted` to decouple update acceptance from update completion:

```csharp
// Client — start the update without waiting for the result
var handle = await workflowHandle.StartUpdateAsync(
    wf => wf.ChatAsync(input),
    new WorkflowUpdateOptions { WaitForStage = WorkflowUpdateStage.Accepted });
// returns as soon as the update is accepted into workflow history

// Poll for the result on its own RPC timeout, retry the wait freely
var response = await handle.GetResultAsync();
```

> **Critical gotcha — RPC timeout ≠ update cancellation.** If `GetResultAsync()` times out, the workflow is **still running** and the update is **still executing**. The client has lost only its return channel. Treat an RPC timeout as "unknown outcome, reconcile via the handle" — not as "failed, retry the update." A naive retry would issue a second `ChatAsync` for the same logical turn and produce duplicate work.

The same pattern exists in the Agents library; see [docs/how-to/MAF/durable-agents.md](../MAF/durable-agents.md) for cross-library symmetry.

---

## Session Lifetime

`SessionTimeToLive` (default: 14 days) controls how long a session workflow remains open while idle. After this period without a new `ChatAsync` call, the workflow exits cleanly. If you then call `ChatAsync` with the same `conversationId`, a new workflow starts — history from the completed workflow is not automatically carried over.

When the Temporal event history for a session grows large (Temporal's per-workflow limit), the library triggers `ContinueAsNew` automatically. The conversation history is serialized into the new workflow run's input and restored before the next turn. This is transparent to callers — the same `conversationId` continues to work.

---

## DurableExecutionOptions Reference

| Option | Type | Default | Description |
|---|---|---|---|
| `TaskQueue` | `string?` | _(required)_ | Temporal task queue. Set automatically by `AddDurableAI` from the worker builder. |
| `ActivityTimeout` | `TimeSpan` | 5 minutes | Start-to-close timeout for LLM call activities. |
| `HeartbeatTimeout` | `TimeSpan` | 2 minutes | Heartbeat timeout for LLM call activities. |
| `RetryPolicy` | `RetryPolicy?` | `null` (Temporal defaults) | Retry policy for activities. When null, Temporal's default unlimited retry applies. |
| `SessionTimeToLive` | `TimeSpan` | 14 days | Inactivity period after which the session workflow exits. |
| `ApprovalTimeout` | `TimeSpan` | 7 days | Maximum time to wait for a human to respond to a HITL tool approval request. |
| `WorkflowIdPrefix` | `string` | `"chat-"` | Prefix prepended to `conversationId` when constructing the Temporal workflow ID. |
| `MaxEntryCount` | `int` | `1000` | Maximum number of `DurableSessionEntry` records the workflow holds before triggering `ContinueAsNew`. Each turn adds two entries (request + response), so the default retains roughly 500 turns. Renamed from `MaxHistorySize` in 0.2.0. |
| `HistoryReducer` | `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?` | `null` | Inline delegate that trims the workflow's entry log at `ContinueAsNew` time. Operates on entries (not flat messages), preserving per-turn `Usage` / `CorrelationId` metadata. **For in-process / unit-test use only** — see note below. |
| `DefaultHistoryReducerKey` | `string?` | `null` | Keyed-DI service key used to resolve the history-reducer delegate on the worker side. The key is serialized into workflow input and survives worker restarts, making this the production-safe alternative to `HistoryReducer`. Register the delegate with `services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(key, ...)` before calling `AddDurableAI`. When both `HistoryReducer` and `DefaultHistoryReducerKey` are set, `DefaultHistoryReducerKey` takes precedence for the durable path. |
| `RegisterDefaultWorkflow` | `bool` | `true` | When `true`, the registrar registers the built-in `DurableChatWorkflow` and `DurableChatSessionClient`. Set to `false` when supplying your own `DurableChatWorkflowBase<TOutput>` subclass — see [Custom Workflow Output](./custom-workflow-output.md). All other infrastructure (DI options, `DurableAIDataConverter`, activities, embeddings) is registered regardless. |
| `MaxToolCallsPerTurn` | `int` | `20` | **Model 3 only.** Maximum LLM↔tool iterations per turn before the workflow synthesizes an "iteration limit exceeded" assistant message. Does not throw — see [Pattern 3 Loop Semantics](#pattern-3-loop-semantics) and [tool-functions.md](tool-functions.md#maxtoolcallsperturn-exhaustion). |
| `MaximumConsecutiveErrorsPerRequest` | `int` | `3` | **Model 3 only.** Number of consecutive failed tool steps tolerated before a non-retryable `ApplicationFailureException` surfaces. Set to `0` for immediate propagation (MAF-style behavior). See [Pattern 3 Loop Semantics](#pattern-3-loop-semantics). |
| `IncludeDetailedErrors` | `bool` | `false` | **Model 3 only.** When `true`, synthesized tool-error `FunctionResultContent` includes exception type and message; when `false`, a generic `"Error: Tool invocation failed."` is used. |

> **`HistoryReducer` is silently non-functional in production durable workflows.**
>
> The `HistoryReducer` delegate is marked `[JsonIgnore]` on the workflow input. It is never serialized to Temporal and is not present when the activity runs after a worker restart or during replay. This means the delegate is silently dropped and the built-in default trim (`DefaultBoundedTrim`) is used instead, even if you have set a custom `HistoryReducer`.
>
> For production durable workflows, use `DefaultHistoryReducerKey` instead:
>
> ```csharp
> // Worker — register the reducer delegate under a stable key in DI
> services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
>     "my-reducer",
>     (sp, key) => history => history.TakeLast(50).ToList());
>
> // Worker — reference the key in DurableExecutionOptions
> builder.Services
>     .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
>     .AddDurableAI(opts =>
>     {
>         opts.DefaultHistoryReducerKey = "my-reducer";
>     });
> ```
>
> The key is serialized into the workflow input and survives continue-as-new transitions and worker restarts. `HistoryReducer` (the delegate) is kept for in-process and unit-test scenarios where serialization is not a concern. Treat a reducer-key change the same as a workflow-code change: in-flight sessions that were started with an older key will continue using that key until they complete or trigger a new continue-as-new.

---

## Composing with Temporal SDK Plugins

> **Experimental:** These APIs carry the `TAI001` diagnostic. Suppress with `#pragma warning disable TAI001` at the call site.

`AddDurableAI()` returns the same `ITemporalWorkerServiceOptionsBuilder`, so you can chain `.AddWorkerPlugin()` and `.AddClientPlugin()` to inject any Temporal SDK plugin (OTel tracing, encryption, custom interceptors) into the registration:

**Worker plugin** — e.g., adding distributed tracing via the OpenTelemetry extension:

```csharp
// Worker
#pragma warning disable TAI001
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddWorkerPlugin(new TracingPlugin());
#pragma warning restore TAI001
```

**Client plugin** — only applies when the worker creates its own client (3-arg overload):

```csharp
// Worker
#pragma warning disable TAI001
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddClientPlugin(new EncryptionPlugin());
#pragma warning restore TAI001
```

For client-only registrations that have no hosted worker, use the `OptionsBuilder<TemporalClientConnectOptions>` overload:

```csharp
// Client
#pragma warning disable TAI001
builder.Services
    .AddTemporalClient("localhost:7233", "default")
    .AddClientPlugin(new EncryptionPlugin());
#pragma warning restore TAI001
```

### Alternative: `DurableAIPlugin` entry point

If you are already composing with other Temporal partner plugins, you may prefer a uniform `.AddWorkerPlugin(...)` registration style. `DurableAIPlugin` is an `ITemporalWorkerPlugin` that registers the same DI services as `AddDurableAI()` — workflow, activities, function registry, session client, and `DurableAIDataConverter` auto-wiring. The two paths are equivalent; `AddDurableAI()` is the recommended primary entry point and remains unchanged.

This pattern matches the canonical "AI Agent SDK" approach from the Temporal AI Partner Ecosystem Guide, which describes plugins as the standard partner integration mechanism.

```csharp
// Worker — equivalent to AddDurableAI() above; pick whichever fits your composition style
#pragma warning disable TAI001
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddWorkerPlugin(new DurableAIPlugin(opts =>
    {
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    }));
#pragma warning restore TAI001
```

`DurableAIPlugin` constructors: `()`, `(Action<DurableExecutionOptions>)`, and `(DurableExecutionOptions)`. Both `DurableAIPlugin` and the `AddWorkerPlugin(DurableAIPlugin)` overload are gated by `[Experimental("TAI001")]`. Mixing `AddDurableAI()` and `AddWorkerPlugin(new DurableAIPlugin())` on the same builder is safe — DI registrations are idempotent and the underlying client-converter plugin is deduplicated by name.

---

## DurableChatSessionClient

`DurableChatSessionClient` is the external entry point for all session interactions. Every method maps to a specific Temporal mechanism on the `DurableChatWorkflow`.

| Method | Temporal mechanism | Purpose |
|--------|--------------------|---------|
| `ChatAsync` | `[WorkflowUpdate]` | Send messages; starts the session workflow if not already running |
| `GetHistoryAsync` | `[WorkflowQuery]` | Retrieve the full conversation history accumulated across all turns |
| `GetPendingApprovalAsync` | `[WorkflowQuery]` | Poll for a blocked HITL approval request; returns `null` if none is pending |
| `SubmitApprovalAsync` | `[WorkflowUpdate]` | Submit a human decision to unblock a pending approval gate |

### ChatAsync

Starts the session workflow on first call using `WorkflowIdConflictPolicy.UseExisting` — subsequent calls with the same `conversationId` reuse the running workflow. Each call delivers messages as a `[WorkflowUpdate]` and blocks until the LLM responds. Returns `Task<DurableSessionResponse>` — the per-turn response entry with `Text`, `Usage`, and `CorrelationId`. The optional `correlationId` parameter lets callers thread a cross-system identifier through the turn.

```csharp
// Client
DurableSessionResponse response = await sessionClient.ChatAsync(
    "conv-123",
    [new ChatMessage(ChatRole.User, "Hello")],
    correlationId: "req-42-abc");   // optional; auto-generated when null
```

### GetHistoryAsync

Returns `IReadOnlyList<DurableSessionEntry>` — every per-turn entry (`DurableSessionRequest` and `DurableSessionResponse`) accumulated across every turn in workflow state order. Each response entry exposes per-turn `Usage` (`UsageDetails?`) and a shared `CorrelationId` linking it to its request. Pattern-match to read the per-turn metadata; `entries.SelectMany(e => e.Messages)` flattens to the prior `ChatMessage` shape.

```csharp
// Client
IReadOnlyList<DurableSessionEntry> history = await sessionClient.GetHistoryAsync("conv-123");
```

### GetPendingApprovalAsync and SubmitApprovalAsync

Used together to implement human-in-the-loop approval gates. Poll `GetPendingApprovalAsync` until a request appears, then call `SubmitApprovalAsync` with a matching `RequestId` to unblock the workflow.

```csharp
// Client
var pending = await sessionClient.GetPendingApprovalAsync("conv-123");
if (pending is not null)
{
    await sessionClient.SubmitApprovalAsync("conv-123", new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
    });
}
```

See [Human-in-the-Loop patterns](hitl-patterns.md) for the full approval flow.

---

## Runnable Example

The `samples/MEAI/DurableChat/` directory contains a complete working sample that demonstrates multi-turn conversation, tool calls, and history queries against a local Temporal server. Start it with:

```bash
temporal server start-dev          # terminal 1
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj   # terminal 2
```

Set the OpenAI API key using `dotnet user-secrets`:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
```

Non-sensitive settings (`OPENAI_API_BASE_URL`, `OPENAI_MODEL`) remain in `samples/MEAI/DurableChat/appsettings.json`. Secrets stored via `dotnet user-secrets` are kept in `~/.microsoft/usersecrets/` (outside the repo) and are automatically loaded by `Host.CreateApplicationBuilder()` in the Development environment.

---

## When to Use Extensions.Agents Instead

`TemporalCommunity.Extensions.AI` is the right choice when you need durable `IChatClient` execution without taking a dependency on the Microsoft Agent Framework. It works with any MEAI-compatible provider and adds Temporal durability to a standard chat pipeline with minimal overhead.

Reach for `TemporalCommunity.Extensions.Agents` when your use case calls for any of the following:

- **LLM-powered routing** — automatically dispatching user messages to a specialist agent based on intent
- **Multi-agent orchestration** — running sub-agents from inside a workflow with `GetAgent` and parallel fan-out with `ExecuteAgentsInParallelAsync`
- **The Microsoft Agent Framework model** — building with `AIAgent`, `ChatClientAgent`, `AgentSessionStateBag`, and `AIContextProvider`
- **Scheduled agent runs** — recurring or deferred agent invocations managed by Temporal Schedules

Both libraries share the same HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) and use the same approval protocol, so an external approval system works against either workflow type.

See [TemporalCommunity.Extensions.Agents Usage Guide](../MAF/usage.md) for the full feature set.
