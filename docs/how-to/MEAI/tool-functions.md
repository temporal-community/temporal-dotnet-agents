# Tool Functions in TemporalCommunity.Extensions.AI

There are three distinct models for invoking tools (AI functions) in this library. The
right one depends on your **intent**, not on a static list of features:

- **"I want a managed durable session"** — you call `DurableChatSessionClient.ChatAsync`
  and let the library own the workflow. Pick **Model 1** (inline tools, single activity
  per turn) or **Model 3** (per-tool activities, dispatch loop owned by the library's
  workflow).
- **"I want to embed durable chat inside my own workflow"** — you author a `[Workflow]`
  and orchestrate calls yourself. Use **Model 2**: the `DurableChatClient` middleware for
  the LLM call plus `.AsDurable()` on each tool so the tool dispatches as its own
  activity.

### Architectural invariant: loops require workflows

A tool-call loop (LLM → tool → LLM → tool → final answer) requires a workflow to
orchestrate it: each iteration must be replay-safe, each fan-out needs an
accumulator, and `Workflow.WhenAllAsync` only exists in workflow context. **Middleware
(`DurableChatClient`) cannot host a loop** — by contract it sees one `GetResponseAsync`
call at a time and dispatches one activity. That is why Model 3 lives exclusively on
`DurableChatSessionClient` (which owns a workflow); Model 2 is the middleware path and
delegates the loop responsibility to your custom workflow code.

---

## Model 1 — Inline Tools in the Chat Pipeline (`UseFunctionInvocation`)

This is the simplest model for `DurableChatSessionClient`. Tools are passed via
`ChatOptions.Tools` and MEAI's `UseFunctionInvocation()` middleware handles the
tool-call loop **inside** the single `DurableChatActivities.GetResponseAsync` activity.

```
DurableChatWorkflow
  └─► DurableChatActivities.GetResponseAsync      ← one Temporal activity
        └─► IChatClient (with UseFunctionInvocation middleware)
              LLM request
              → tool call (executed locally in the activity)
              → LLM request with tool result
              → final response
```

The entire round-trip — LLM request, tool execution, follow-up LLM request — runs inside
a **single activity**. From Temporal's perspective, the chat turn is one unit of work.

### Setup

Register `UseFunctionInvocation()` on the `IChatClient` pipeline, then pass tools per
call via `ChatOptions`:

```csharp
// Worker
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()   // handles the tool call loop inside the activity
    .Build();

builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI();
```

```csharp
// Client
var getWeather = AIFunctionFactory.Create(
    (string city) => $"Sunny, 22°C in {city}",
    name: "get_current_weather");

var options = new ChatOptions { Tools = [getWeather] };
var response = await sessionClient.ChatAsync("conv-123", messages, options);
```

### When to use

- The tool is fast and lightweight
- You want a simple setup with no custom workflow code
- Per-tool retry/observability is not a requirement — retrying the whole chat turn on
  failure is acceptable
- Most applications should start here

### What `AddDurableTools` does NOT do in Model 1

`AddDurableTools` and `AsDurable()` have **no effect** in this path. `UseFunctionInvocation()`
calls tool functions directly inside the activity process — it never touches the
`DurableFunctionRegistry`. If you want each tool to be its own Temporal activity
(observability, independent retry) without writing a custom workflow, see Model 3.

---

## Model 2 — Per-Tool Temporal Activities Inside a Custom Workflow (`AsDurable()`)

This model is for **custom `[Workflow]` code** that needs to invoke a tool as its own
independent Temporal activity. Each tool call gets a separate entry in the event history,
with its own retry policy, timeout, and failure isolation.

```
MyCustomWorkflow
  └─► durableTool.InvokeAsync(arguments)          ← dispatches to...
        └─► DurableFunctionActivities              ← its OWN Temporal activity
              └─► DurableFunctionRegistry["tool-name"] → real function
```

`AsDurable()` wraps an `AIFunction` as a `DurableAIFunction`. When `InvokeAsync` is
called inside a workflow (`Workflow.InWorkflow == true`), it dispatches to
`DurableFunctionActivities` via `Workflow.ExecuteActivityAsync`. Outside a workflow
(`Workflow.InWorkflow == false`), it passes through to the inner function unchanged —
the same wrapped instance works in both contexts.

### Setup

Register the real function with `AddDurableTools` so `DurableFunctionActivities` can
resolve it by name at runtime:

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI()
    .AddDurableTools(getWeather);   // puts function in DurableFunctionRegistry
```

Inside the workflow, wrap with `AsDurable()` and call `InvokeAsync`:

```csharp
// Workflow
[Workflow]
public class MyWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string city)
    {
        // The inner lambda is a stub — only reached when Workflow.InWorkflow == false.
        // Inside this workflow, InvokeAsync dispatches to DurableFunctionActivities.
        var durableWeather = AIFunctionFactory.Create(
            (string c) => "[stub]",
            name: "get_current_weather"
        ).AsDurable();

        var result = await durableWeather.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = city }));

        return result?.ToString() ?? string.Empty;
    }
}
```

The function name passed to `AIFunctionFactory.Create` must match the name registered
via `AddDurableTools` — `DurableFunctionActivities` resolves by name (case-insensitive).

> **Prerequisites for `AsDurable()`.** The activity worker handling the workflow's task
> queue must have both `AddDurableAI()` (registers the dispatch activity) and
> `AddDurableTools(function)` (registers the function in the durable function registry)
> called on it. These are runtime requirements — `AsDurable()` itself has no DI context
> and cannot validate them at wrap time. If the function is missing from the registry,
> the activity throws `InvalidOperationException` at invocation time with the message
> `"Function '{name}' is not registered in the durable function registry."`

### When to use

- The tool is **long-running** or calls a slow external API that may time out
- You want **independent retry** per tool call — a failed tool should not force the
  whole workflow to retry from the start
- Different tools need **different timeout or retry policies**
- You want each tool invocation to appear as a **named, inspectable entry** in the
  Temporal Web UI or event history
- You are writing a **custom orchestration workflow** — that is, you are the orchestrator,
  not `DurableChatSessionClient`

### Per-tool timeout and retry

Pass `ActivityOptions` to `AsDurable()` to override options per function:

```csharp
// Workflow
var durablePayment = paymentTool.AsDurable(new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 3 }
});

var durableLookup = lookupTool.AsDurable(new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(5),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 10 }
});
```

---

## Model 3 — Durable Tool Dispatch in the Chat Pipeline (`AddDurableTools` without `UseFunctionInvocation`)

Model 3 gives you Model 2's per-tool observability and retry **without writing a custom
workflow**. Register tools with `AddDurableTools()`, **omit** `UseFunctionInvocation()`
from the chat client chain, and `DurableChatSessionClient` automatically runs the
dispatch loop inside its own workflow:

```
DurableChatWorkflow.ExecuteTurnAsync
  └─► [loop until IsFinal or MaxToolCallsPerTurn exceeded]
        └─► GetChatStepAsync activity          ← one LLM call per iteration
              ← FunctionCallContent items (if any)
        └─► InvokeFunctionAsync × N activities ← one activity per tool, dispatched in parallel
              ← FunctionResultContent items
        └─► accumulate tool results into messages, loop back
```

Each LLM call **and** each tool call appear as separate activities in workflow history.
The workflow owns the orchestration; the activities are leaf workers.

### Activation

Model 3 is **auto-detected** at session start: when `DurableFunctionRegistry.Count > 0`
(i.e., you registered at least one tool via `AddDurableTools`), `DurableChatSessionClient`
eagerly resolves per-tool `ActivityOptions` for every registered tool and ships the
complete dictionary into `DurableChatWorkflowInput.ToolActivityOptions`. The workflow
sees this dict is populated and switches to the dispatch loop.

When the registry is empty (no `AddDurableTools` calls), the workflow falls back to the
Model 1 single-activity path — fully backward compatible.

### Setup

```csharp
// Worker — note: NO .UseFunctionInvocation() on the chat client
builder.Services
    .AddChatClient(innerClient)
    .Build();

var weatherTool = AIFunctionFactory.Create(
    (string city) => $"Sunny, 22°C in {city}",
    name: "get_weather");

var stockTool = AIFunctionFactory.Create(
    (string symbol) => GetStockQuote(symbol),
    name: "get_stock_quote");

builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI(opts =>
    {
        opts.MaxToolCallsPerTurn = 10;                 // cap loop iterations (default 20)
        opts.MaximumConsecutiveErrorsPerRequest = 3;   // default 3; set 0 to propagate immediately
        opts.IncludeDetailedErrors = false;            // default false
    })
    .AddDurableTools(weatherTool)
    .AddDurableTools(stockTool, opts => opts
        .NoRetry()
        .WithTimeout(TimeSpan.FromSeconds(30)));
```

### Calling — auto-population vs. explicit tool subset

The client side has two equivalent shapes:

```csharp
// Option A — let the activity auto-populate ChatOptions.Tools from the registry.
// All tools registered via AddDurableTools are advertised to the LLM.
var response = await sessionClient.ChatAsync(conversationId, messages);

// Option B — explicitly pass a subset. The caller's explicit choice is respected;
// auto-population only runs when ChatOptions.Tools is null or empty.
var response = await sessionClient.ChatAsync(
    conversationId,
    messages,
    new ChatOptions { Tools = [weatherTool] });   // only weather, not stock
```

### When to use

- You want **per-tool observability** — every tool call as its own Web-UI/event-history entry
- You want **per-tool retry/timeout** — long-running API calls with independent retry policies
- You **do not want to write a custom workflow** — `DurableChatSessionClient` is good enough
- A tool error should be **fed back to the LLM** (default) so the model can recover, not
  surfaced as an exception immediately (configurable via `MaximumConsecutiveErrorsPerRequest`)

### Per-tool options (`DurableChatToolOptions`)

`AddDurableTools` has an overload that takes a configuration callback:

```csharp
.AddDurableTools(myTool, opts => opts
    .NoRetry()                           // MaximumAttempts = 1
    .WithMaxAttempts(5)                  // sets MaximumAttempts on RetryPolicy
    .WithTimeout(TimeSpan.FromSeconds(60))); // sets StartToCloseTimeout
```

Power users (custom backoff, non-retryable error types) assign `RetryPolicy` directly:

```csharp
.AddDurableTools(myTool, opts =>
{
    opts.RetryPolicy = new RetryPolicy
    {
        MaximumAttempts = 5,
        InitialInterval = TimeSpan.FromSeconds(1),
        BackoffCoefficient = 2.0,
        NonRetryableErrorTypes = new[] { typeof(InvalidOperationException).FullName! },
    };
});
```

> **Write-style tool guidance.** For non-idempotent tools (send email, charge card, write
> database record), pass `opts => opts.NoRetry()`. Otherwise a transient failure between
> "tool executed" and "Temporal recorded the result" can cause double-execution on retry.

### Mid-session drift warning

**Per-tool options are frozen at session start.** When `DurableChatSessionClient.ChatAsync`
creates the workflow, it eagerly resolves the full `ToolActivityOptions` dict from the
registry and captures it in workflow history. Adding a new tool via `AddDurableTools` after
that point does **not** affect the already-running session — the new tool will only be
picked up by sessions started *after* the registration. This is required for replay
determinism: a workflow replaying on a different worker process must see the same options
that were active when the session began.

Practical implication: `DurableChatSessionClient` caches the per-tool options snapshot on first use — it is computed once for the lifetime of the client instance (thread-safe via `Lazy<T>`). All `AddDurableTools` calls must therefore complete before the host starts, not merely before the first `ChatAsync` for a given conversation. For typical static registrations at worker startup this is automatic. Dynamic late registration is not supported.

### Long-running turns — RPC timeout vs. update cancellation

Temporal's update RPC has a default **~60-second** timeout. A Model 3 turn that loops
through several tool calls can exceed this without the workflow having anything wrong with
it — the LLM is just taking a while to converge.

The canonical pattern: start the update with `WaitForStage.Accepted`, then poll its result:

```csharp
// Client — start the update; do not wait for completion
var handle = await workflowHandle.StartUpdateAsync(
    wf => wf.ChatAsync(input),
    new WorkflowUpdateOptions { WaitForStage = WorkflowUpdateStage.Accepted });
// returns as soon as the update is accepted into workflow history

// Poll for the result — has its own (longer) RPC timeout, can be retried freely
var response = await handle.GetResultAsync();
```

> **Critical gotcha — RPC timeout ≠ update cancellation.** If `GetResultAsync()` times
> out, the workflow is **still running** and the update is **still executing**. The
> client has only lost its return channel. Treat an RPC timeout as "unknown outcome,
> reconcile via the handle" — not as "failed, retry the update." A naive retry would
> issue a second `ChatAsync` for the same logical turn and produce duplicate work.

### Observability

Model 3 emits **multiple existing spans per turn** — no new OTel surface to learn:

- One `chat {modelId}` span per LLM iteration (inside `GetChatStepAsync`)
- One `execute_tool {toolName}` span per tool invocation (inside `InvokeFunctionAsync`)
- All correlated by the `conversation.id` span attribute and per-turn `correlationId`

Filter by `conversation.id` in your tracing backend to see every span produced by one
Model 3 turn. The existing `TurnCount` and `SessionCreatedAt` search attributes
continue to work at the session level — no new search attributes needed. The workflow
itself emits no diagnostic spans (workflow code must stay deterministic on replay).

### Error handling — catch-and-feed-back (default)

When a tool activity fails, Model 3's default behavior **mirrors Pattern 1's
`FunctionInvokingChatClient`**: synthesize a `FunctionResultContent` with an error
message and feed it back to the LLM so the model can recover (apologize, try a different
tool, ask the user for clarification). The workflow keeps a per-turn
`consecutiveErrors` counter:

- After a step where any tool fails, `consecutiveErrors++`
- If `consecutiveErrors > MaximumConsecutiveErrorsPerRequest` (default 3), the workflow
  throws a non-retryable `ApplicationFailureException` so the caller sees the failure
- If the next step's tools all succeed, the counter resets to 0
- Set `MaximumConsecutiveErrorsPerRequest = 0` for immediate propagation (MAF-style
  behavior — one tool failure surfaces directly)

`IncludeDetailedErrors` (default `false`) controls whether the synthesized error message
includes the exception type and message, or a generic `"Error: Tool invocation failed."`.
Set to `true` only when LLM error context is more valuable than tightening the
information surface.

> **Parallel fan-out invariant.** When multiple tool calls are dispatched in parallel
> from one step, the workflow synthesizes a `FunctionResultContent` for **every**
> `CallId` in original order, even for the ones that failed. OpenAI/Anthropic reject
> tool turns with missing call IDs; this preserves protocol compatibility.

### `MaxToolCallsPerTurn` exhaustion

If the LLM keeps requesting tools and the loop exceeds `MaxToolCallsPerTurn` (default
20), the workflow does **not** throw. It synthesizes an explicit assistant message
("Maximum tool-call iterations (N) exceeded; the conversation did not converge on a
final answer."), appends it to history, and returns a normal `ChatResponse`. This:

- Preserves workflow history consistency
- Gives the caller a clear, debuggable signal rather than a partial response
- Logs a warning for diagnostics

Raise `MaxToolCallsPerTurn` if your tools legitimately require many iterations; lower it
to fail fast if you suspect a tool-call loop.

### Tool serialization contract

Tool inputs (`DurableFunctionInput.Arguments`, an `IDictionary<string, object?>`) and
outputs (`DurableFunctionOutput.Result`, an `object?`) round-trip via
`AIJsonUtilities.DefaultOptions` (registered through `DurableAIDataConverter`).
**Arguments and return values must be JSON-serializable.** Complex objects must have
JSON serialization shapes that the converter can round-trip; primitives, records,
DTOs, and standard collections work out of the box. This is the same contract as
Model 2 (no new design).

---

## Common Pitfalls

### Silent-failure footgun — `DurableToolsNotWrappedException`

The trap: you have a **custom workflow** using `DurableChatClient` middleware (Model 2
territory), you call `AddDurableTools(myTool)`, but you forgot to wrap the tool with
`.AsDurable()` in your workflow code. The LLM returns `FunctionCallContent`, no
dispatch handler is wired up, and the message rides quietly through the chat pipeline
with no tool ever invoked.

The library catches this at runtime. `DurableChatActivities.GetResponseAsync` checks
after every LLM call: if `DurableFunctionRegistry` has entries, the response contains
`FunctionCallContent`, and `FunctionInvokingChatClient` is **not** in the chain, it
throws `DurableToolsNotWrappedException`:

> LLM returned tool calls but no dispatch handler is configured. Either (1) use
> `DurableChatSessionClient` instead of `DurableChatClient` middleware, (2) wrap tools
> with `.AsDurable()` in your custom workflow code (Pattern 2), or (3) use
> `UseFunctionInvocation()` in the chat client chain (Pattern 1).

The fix is whichever of those three matches your intent.

### "I registered tools via `AddDurableTools()` but my custom workflow doesn't dispatch them"

This is the same silent-failure scenario above. `AddDurableTools` registers in the
`DurableFunctionRegistry` — it does **not** wire the registry into your custom workflow's
LLM calls. You must either switch to `DurableChatSessionClient` (Model 3 takes over the
loop for you) or wrap each tool with `.AsDurable()` in your workflow (Model 2).

### "My new tool isn't being called by an already-running session"

See **Mid-session drift warning** above. Per-tool options and the registered tool set
are frozen at session start. Start a new session (different `conversationId`) to pick up
new registrations.

### Mixing `UseFunctionInvocation()` + `AddDurableTools()`

This combination is rejected at startup by `DurableMixedPatternValidator` with
`DurableMixedPatternException`. The two patterns are mutually exclusive — pick Model 1
(FIC, no `AddDurableTools`) or Model 3 (`AddDurableTools`, no FIC). Model 2 is exempt
because it does not use `DurableChatSessionClient`.

---

## Comparison

| | Model 1 (`UseFunctionInvocation`) | Model 2 (`AsDurable()`) | Model 3 (`AddDurableTools`) |
|---|---|---|---|
| Entry point | `DurableChatSessionClient.ChatAsync` | Custom `[Workflow]` | `DurableChatSessionClient.ChatAsync` |
| Tool execution | MEAI middleware inside one activity | `DurableFunctionActivities` — own activity per call | `DurableFunctionActivities` — own activity per call (workflow-coordinated loop) |
| Temporal event history | One entry for the whole chat turn | One entry per tool invocation | One entry per LLM call + one per tool invocation |
| Per-tool retry / timeout | No | Yes (via `ActivityOptions`) | Yes (via `DurableChatToolOptions`) |
| Code complexity | Low | Requires custom workflow | Low (no custom workflow) |
| Typical use case | Simple chat with tools | Long-running tools, custom pipelines | Chat with observable tools, no custom workflow |

---

## Sample Code

- **Model 1**: `samples/MEAI/DurableChat/` — Demo 2 (`RunToolCallDemoAsync`) shows
  `ChatOptions.Tools` with `UseFunctionInvocation()`.
- **Model 2**: `samples/MEAI/DurableTools/` — `WeatherReportWorkflow` shows `AsDurable()`
  inside a custom workflow dispatching to `DurableFunctionActivities`.
- **Model 3**: `samples/MEAI/DurableChat/` — Demo 3 (`RunDurableToolDemoAsync`) shows
  `AddDurableTools()` auto-dispatch through `DurableChatSessionClient` with per-tool
  options and Temporal Web UI verification of separate `InvokeFunction` activities.
