# Extensible durable turns

## Method receiver ownership

`AddDurableToolFactory<THandler>(methodName, ...)` uses MEAI's instance receiver factory. Schema
construction and the compiled `ActivatorUtilities` factory happen once during worker registration.
Each invocation resolves constructor dependencies from the current activity scope and creates a new
handler. MEAI disposes that invocation-owned handler after the method completes; the activity scope
separately disposes its scoped dependencies. The library never returns a container-owned handler to
MEAI, avoiding double disposal. Success, failure, and cancellation follow the same lifetime rule.

The activity sets `AIFunctionArguments.Services` to its scoped provider. MEAI automatically excludes
`IServiceProvider`, `AIFunctionArguments`, and `CancellationToken` parameters from the model schema.

This document records the design constraints for application-owned workflows that reuse the
package's managed model/tool loop. The feature is intentionally based on ordinary
`Microsoft.Extensions.AI.AIFunction` instances. It does not introduce a second middleware pipeline,
a mandatory tool-result wrapper, ambient application state, or an application-result mapper.

## Declaration before implementation

The model activity needs a function declaration before any tool is invoked. An implementation
factory, by contrast, must run inside the tool activity so it can use that activity attempt's
application request/state values. Therefore the durable workflow freezes a declaration
snapshot separately from the activity-local implementation.

`AddDurableChatWorkflowInputFactory` installs the same default-preserving client converter
configuration as `AddDurableAI`. This affects clients built through `AddTemporalClient` regardless
of registration order. Manually connected clients remain outside that options pipeline and must
set `DurableAIDataConverter.Instance` themselves.

In a split deployment, the workflow-starting process needs a Temporal client, while an
implementation-only worker can use the client created by the three-argument
`AddHostedTemporalWorker` registration. Disabling `RegisterDefaultWorkflow` means that worker does
not construct the stock session client and therefore does not require a separate `ITemporalClient`
service in its container.

The snapshot contains the function name, description, parameter schema, return schema, and
deterministic structural fingerprints. Object-property order is normalized ordinally; array order
and scalar values remain significant. This is deliberately a structural comparison, not a general
JSON Schema equivalence algorithm.

The factory-created implementation must have the same ordinal name and the same parameter and
return fingerprints. A mismatch is a non-retryable configuration failure and the function is not
invoked.

The registered factory delegate is invoked once per activity attempt and receives that attempt's
scoped `IServiceProvider`. Services resolved from it follow ordinary .NET DI lifetimes, and the
scope—including disposable scoped dependencies—is disposed when the attempt ends. A retry creates
a new scope and a new function/decorator chain. Do not capture the provider or a scoped dependency
beyond that invocation. Use ordinary MEAI function decoration for activity-local behavior, and do
not treat factory or wrapper fields as durable session state.

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

An execution adapter is ordinary MEAI function middleware, not a second Temporal middleware
abstraction. The invocation factory may wrap its ordinary `AIFunction` in a
`DelegatingAIFunction`; the activity invokes the resulting function inside the attempt's DI scope.
Before/success/error/finally hooks therefore run once per activity attempt. Adapters must rethrow
errors and cancellation so the activity records failure and Temporal—not the decorator—owns retry.
Authorization belongs immediately before the external effect and must consult authoritative,
current application data; request data and turn state only locate that decision.

Generalized missing-input waits are not part of this API. See the non-shipping
[deferred-tool research decision](generalized-deferred-tools-research.md) for the evaluated state
machine and the criteria that must be met before any public design is proposed.

## Package-owned orchestration boundary

The stock managed workflow and the typed specialization share one loop implementation on
`DurableChatWorkflowBase<TOutput>`. The loop itself remains internal; the public typed base and its
request, result, dispatch, activation, and state-completion contracts are the supported seam.

This extraction does not alter the shipped command sequence. The loop still performs one model
activity per iteration, resolves every interceptor and approval decision before starting any tool,
fans approved tools out in parallel, records one activity per real tool call, and reassembles
synthetic and real results in original model-call order. It performs no workflow-side service
resolution, I/O, or application delegate invocation.

## Specialized workflow lifecycle

`DurableToolWorkflowBase<TRequestData, TTurnState>` owns the managed-turn lifecycle in addition to
the model/tool loop. One workflow Update may call `RunDurableTurnAsync` exactly once. A second call
from the same Update fails that Update non-retryably before another model or tool activity is
scheduled. The Update remains consumed even when its first managed turn failed and application
code caught that failure. Different Update IDs in the same run may each execute one turn.
Null requests, empty message lists, and null turn options fail terminally as
`DurableTurnInvalidRequest` before any model or tool activity is scheduled; they are application
input failures, not workflow-task failures.

When a typed turn reaches the model/tool iteration limit, the immediate `DurableTurnResult`
contains the complete function-call/function-result protocol and final turn state. That output is
for caller inspection and explicit commit/discard policy. The workflow persists only the terminal
assistant sentinel in conversation history, so a later turn cannot observe tool results whose
typed state the caller discarded. A stable `Workflow.Patched` marker preserves replay of 0.12.0
histories that stored the complete capped protocol; normal final turns do not emit that marker.

Continue-as-New preserves the concrete workflow type and the canonical frozen
`DurableChatWorkflowInput`, including declaration snapshots, keyed reducer selection, tool and
interceptor activity policies, approval policy, and model/tool limits. Update IDs are scoped to one
run, so the one-turn guard starts with an empty set after Continue-as-New. A checked-in typed-turn
history containing `GetChatStep`, `InvokeFunction`, state completion, and a final `GetChatStep`
replays against the source-linked compatibility workflow in the server-free unit lane.
