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
4. Managed tools default to one activity attempt. Multiple attempts require `Idempotent()` and
   downstream deduplication using the logical-call key; use `NoRetry()` to explicitly keep one
   attempt even for an idempotent function.
5. Use `ResolveApprovalAsync(...)` to submit matching pending approval decisions. Non-progressing
   decisions are rejected before acceptance; reconcile ambiguous outcomes with application-owned
   review state instead of retrying blindly.

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

Managed tool results are checked against `MaximumTransportPayloadSizeBytes` before the activity
completes. If the function has returned successfully but the serialized result is too large, the
activity records a bounded `ResultTooLarge` outcome and the model receives an error result with
the same call ID; the tool is not retried just to obtain a smaller response. Return compact values
or an external reference for large data. Activity failure messages are also bounded and do not
carry original exception details or stack traces across the Temporal boundary.

Failure sanitization retains ordinary CLR exception class names for Temporal
`RetryPolicy.NonRetryableErrorTypes` matching and retains an `ApplicationFailureException`'s
`NextRetryDelay`. Explicit permanent provider and result-transport classifications keep their
library-defined error types. Dynamic application error types remain UTF-8 bounded; use short,
stable error identities rather than embedding exception content in them.

Permanent model-step failures, including `DurableContextBudgetExceeded`,
`DurableContextReducerNotFound`, and `DurableContextReducerInvalidOutput`, end the turn with
their specific error type. They do not consume the managed consecutive-error allowance or
schedule another model activity. The same applies to application failures explicitly marked
non-retryable or excluded by the model activity's `NonRetryableErrorTypes`. Retryable failures
still use the configured activity retry policy and then the managed consecutive-error allowance.

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
var pending = await sessionClient.GetPendingApprovalAsync("customer-42")
    ?? throw new InvalidOperationException("No approval is pending.");
var result = await sessionClient.ResolveApprovalAsync(
    "customer-42",
    new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        CallId = pending.CallId,
        FunctionName = pending.FunctionName,
        Approved = true,
    });
```

`ResolveApprovalAsync` returns a `DurableApprovalResolutionResult` for an accepted decision.
Unknown, stale, already-resolved, and conflicting decisions are rejected by the workflow update
validator before they enter history; handle the resulting update failure and reconcile using
application-owned review state. Copy the call ID and function name from the pending request;
both fields are required for a tool-call request, and a mismatch is rejected. Use
`DurableApprovalMeaiAdapter` when mapping to MEAI's approval content types so the original
function-call identity is retained.

Tool exposure is not business authorization. Unknown and unselected model calls receive the same
safe blocked result and no tool activity is scheduled. For external writes, reauthorize using
current authoritative application data inside the tool activity immediately before the effect.

Managed sessions retain at most 128 terminal request outcomes or 1 MiB of serialized outcomes by
default; configure `MaxTerminalOutcomeCount` and `MaxTerminalOutcomeBytes` on
`DurableExecutionOptions` when a different recovery window is needed. The oldest terminal outcome
is evicted first, while pending turns are never evicted. An oversized outcome is not retained.
After eviction, retrying that request ID can execute the turn again, so tool-side business
idempotency remains necessary even when request IDs are reused correctly.

### Cancelling a managed turn

Call `CancelTurnAsync(conversationId, requestId)` to request workflow-owned cancellation of the
matching active turn. The request ID must match the one supplied to `SendAsync`. A mismatched or
already completed request is rejected during update validation; a completion race may instead
return `AlreadyCompleted`. Cancelling the caller's `SendAsync` RPC wait does not cancel accepted
workflow work. The workflow propagates cancellation to an active model, interceptor, or tool
activity and interrupts a pending approval wait. It waits for active activity cancellation to
complete before stopping and does not schedule more activity work. Activity code and downstream
operations must cooperate with cancellation; an external effect already dispatched is not undone
and may need reconciliation.

If dispatch occurred before the turn failed or was cancelled, the workflow retains an interrupted
response for later model context: completed results remain paired with their calls, uncertain
dispatched outcomes are marked unknown, and undispatched calls are marked not executed. Partial
assistant text is dropped. This record is bounded and redacted; it is not an external-effect audit
log or a guarantee that an unknown result did not commit.
