# MEAI managed-session tool contract

`TemporalCommunity.Extensions.AI` has one managed-session tool model: register every callable
function with `AddDurableTools`. The workflow supplies those schemas to the model and dispatches
each model-requested invocation as an `InvokeFunction` Temporal activity.

## Required setup

1. Register a bare `IChatClient` for the durable session.
2. Register each callable function with `AddDurableTools(...)` on every worker that serves the
   task queue.
3. Keep function names and schemas stable across workers serving the same task queue.
4. Use `NoRetry()` for a side-effecting function when an activity retry would be unsafe; otherwise
   make the function idempotent.
5. Use `ResolveApprovalAsync(...)` for managed-session approval decisions when the caller needs a
   retry-safe resolution status.

## Unsupported managed-session configuration

- Do not add `UseFunctionInvocation()` to the session `IChatClient` pipeline.
- Do not set `ChatOptions.Tools` on `DurableChatSessionClient.SendAsync` requests. The client
  rejects it because caller-owned delegates cannot cross the durable workflow boundary.

`AIFunction.AsDurable()` remains available for a custom workflow that explicitly invokes a known
function. It is separate from managed chat sessions and does not make caller-supplied
`ChatOptions.Tools` supported.
