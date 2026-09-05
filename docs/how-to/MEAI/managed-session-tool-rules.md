# Managed-session tool rules

`TemporalCommunity.Extensions.AI` has one worker-owned managed-session tool model. Use
`AddDurableTool`/`AddDurableTools` for one implicit default group, or `AddDurableToolset` plus
`DefaultToolsetIds` for several named groups. The thin client carries neither schemas nor
implementations. The workflow records one resolved manifest, supplies its declarations to the
model, and dispatches each enabled invocation as an `InvokeFunction` Temporal activity.

## Required setup

1. Register a bare `IChatClient` for the durable session.
2. Register one callable function through `AddDurableTool(...)`, a uniform collection through
   `AddDurableTools(...)`, or a named group through `AddDurableToolset(...)` on
   every worker that serves the task queue.
3. Keep function names and schemas stable across workers serving the same task queue.
4. Use `NoRetry()` for a side-effecting function when an activity retry would be unsafe; otherwise
   make the function idempotent.
5. Use `ResolveApprovalAsync(...)` for managed-session approval decisions when the caller needs a
   retry-safe resolution status.

The minimal shape for step 2 — a bare chat client plus one worker-owned tool:

```csharp
builder.Services.AddChatClient(innerChatClient); // no UseFunctionInvocation()

var weatherTool = AIFunctionFactory.Create(
    (string city) => weather.GetCurrent(city),
    name: "get_weather",
    description: "Gets current weather for a city.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI()
    .AddDurableTool(weatherTool);
```

`AddDurableTool` and `AddDurableTools` both contribute to one implicit default toolset. To expose
several named groups instead — e.g. so a custom workflow can select a subset per turn — use
`AddDurableToolset(id, ...)` and set `DefaultToolsetIds` on `DurableExecutionOptions`:

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options => options.DefaultToolsetIds = ["weather"])
    .AddDurableToolset("weather", toolset => toolset.Add(weatherTool));
```

## Unsupported managed-session configuration

- Do not add `UseFunctionInvocation()` to the session `IChatClient` pipeline.
- Do not set `ChatOptions.Tools` on `DurableChatSessionClient.SendAsync` requests. The client
  rejects it because caller-owned delegates cannot cross the durable workflow boundary.

```csharp
// Wrong: the workflow, not the pipeline, owns the model/tool loop for a managed session.
builder.Services
    .AddChatClient(innerChatClient)
    .UseFunctionInvocation();
```

```csharp
// Wrong: caller-supplied ChatOptions.Tools cannot cross the durable workflow boundary.
var options = new ChatOptions { Tools = [weatherTool] };
await sessionClient.SendAsync("customer-42", messages, options); // throws
```

Both configurations fail fast rather than silently falling back to an inline tool loop, so a
misconfigured worker or caller finds out at startup or on the first request, not partway through a
turn.

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is separate from managed chat sessions and does not make caller-supplied
`ChatOptions.Tools` supported.

### `NoRetry()` for side-effecting tools

A tool with an external, non-idempotent effect (send email, charge a card, post a message) should
opt out of Temporal's default activity retries so a transient failure does not re-run the effect:

```csharp
var sendEmailTool = AIFunctionFactory.Create(
    (string to, string body) => mailer.Send(to, body),
    name: "send_email",
    description: "Sends an email to a customer.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI()
    .AddDurableTool(sendEmailTool, tool => tool.NoRetry());
```

### `ResolveApprovalAsync(...)` for pending approvals

When a tool requires human approval before dispatch, resolve the pending request through the
session client rather than any ad hoc channel:

```csharp
var result = await sessionClient.ResolveApprovalAsync(
    "customer-42",
    new DurableApprovalDecision { RequestId = requestId, Approved = true });
```

`ResolveApprovalAsync` returns a `DurableApprovalResolutionResult` so the caller can distinguish a
successful resolution from a stale or already-resolved request instead of guessing from a thrown
exception.

Tool exposure is not business authorization. Unknown and unselected model calls receive the same
safe blocked result and no tool activity is scheduled. For external writes, reauthorize using
current authoritative application data inside the tool activity immediately before the effect.
