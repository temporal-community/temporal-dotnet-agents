# Extensible durable turns

This document records the design constraints for application-owned workflows that reuse the
package's managed model/tool loop. The feature is intentionally based on ordinary
`Microsoft.Extensions.AI.AIFunction` instances. It does not introduce a second middleware pipeline,
a mandatory tool-result wrapper, ambient application state, or an application-result mapper.

## Declaration before implementation

The model activity needs a function declaration before any tool is invoked. An implementation
factory, by contrast, must run inside the tool activity so it can use that activity attempt's DI
scope and application request/state values. Therefore the durable workflow freezes a declaration
snapshot separately from the activity-local implementation.

The snapshot contains the function name, description, parameter schema, return schema, and
deterministic structural fingerprints. Object-property order is normalized ordinally; array order
and scalar values remain significant. This is deliberately a structural comparison, not a general
JSON Schema equivalence algorithm.

The factory-created implementation must have the same ordinal name and the same parameter and
return fingerprints. A mismatch is a non-retryable configuration failure and the function is not
invoked.

## Ordinary functions remain ordinary

Only model-supplied method parameters appear in the function schema and argument dictionary.
Application request data and turn state are captured when the tool activity creates the ordinary
function. Existing `DelegatingAIFunction` decorators remain part of that function chain and execute
normally. The same undecorated or decorated function can still be invoked outside Temporal.

MEAI 10.8.3 marshals the result of a reflected `AIFunction` through its configured result marshaller.
With the standard marshaller, the object returned by `AIFunction.InvokeAsync` is a `JsonElement`,
including for a source method that returns a .NET string. A post-success state completion operation
therefore observes the activity-side, model-visible marshalled result rather than the original CLR
return value. The public callback contract must preserve that MEAI boundary.

State completion runs only after the ordinary function succeeds. It has two explicit outcomes:
leave the turn state unchanged, or replace it with a supplied value. Replacing state with `null` is
different from leaving it unchanged. A completion-bearing activation is invalid under parallel
dispatch and is rejected before function invocation.

## Additional properties policy

The first version requires both the frozen declaration and every factory-created implementation to
have an empty `AITool.AdditionalProperties` dictionary. Registration reports the tool name and
ordinally sorted property keys. An activity-local implementation violation is a non-retryable
configuration failure before invocation.

`AdditionalProperties` contains arbitrary `object?` values. Silently dropping entries would change
model or provider behavior, while silently JSON-normalizing them would change their runtime types
and may fail for non-serializable values. The package will not claim support until it has a reviewed,
typed, deterministic policy. Applications whose function behavior depends on these properties
cannot use invocation-scoped durable tools in the first version.

## Rejected shapes

- A mandatory `DurableToolResult<T>` would change ordinary function return schemas and behavior.
- `MapApplicationResult` duplicates the single post-success state completion operation.
- `EmitEffect` and an application-item accumulator duplicate turn state and imply another result
  channel.
- A Temporal-specific function middleware abstraction duplicates MEAI's `DelegatingAIFunction`.
- Public ambient state would be unsafe across activity attempts and concurrent workflow Updates.

The durable orchestration layer owns scheduling, approvals, retries, and state threading. MEAI
function decorators remain the activity-local extension point for validation, authorization,
telemetry, and other function behavior.

## Package-owned orchestration boundary

The stock managed workflow and the forthcoming typed specialization share one loop implementation
on `DurableChatWorkflowBase<TOutput>`. The operation remains internal until the typed request,
result, dispatch, and completion contracts are complete.

This extraction does not alter the shipped command sequence. The loop still performs one model
activity per iteration, resolves every interceptor and approval decision before starting any tool,
fans approved tools out in parallel, records one activity per real tool call, and reassembles
synthetic and real results in original model-call order. It performs no workflow-side service
resolution, I/O, or application delegate invocation.
