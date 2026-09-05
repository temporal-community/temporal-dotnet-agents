# The Direct-Adapter-in-Workflow Anti-Pattern

This document is a **design-decision record and guardrail**, not a how-to. It explains why
`TemporalCommunity.Extensions.AI` does **not** support constructing a durable chat/embedding
adapter directly inside workflow code, so that nobody re-proposes the shape without understanding
why it was rejected. For the supported patterns, see [MEAI usage](../../how-to/MEAI/usage.md) and
the package [README](../../../src/TemporalCommunity.Extensions.AI/README.md).

## Quick Answer

- ❌ **Don't** construct `DurableChatClient` / `DurableEmbeddingGenerator` — or any
  `ChatClientBuilder` / `EmbeddingGeneratorBuilder` composition wrapping either — inside a
  `[WorkflowRun]` or `[WorkflowUpdate]` method.
- ✅ **Do** use one of the three supported patterns instead:
  1. **Managed session** (`DurableChatSessionClient` / `AddDurableAI()`) — the stock workflow owns
     the whole conversation.
  2. **`DurableChatWorkflowBase<TOutput>` / `DurableToolWorkflowBase<TRequestData, TTurnState>`** —
     you own the workflow class, but keep the package's session/history/HITL/continue-as-new
     machinery.
  3. **Hand-written Activity + `AIFunction.AsDurable()`** — you own the workflow class and want
     none of the machinery above; a constructor-injected `IChatClient` in a plain Activity handles
     the LLM call, and `AsDurable()` handles individual durable tool calls.

Each is indexed below. The rest of this doc explains the *why* behind retiring direct-adapter
construction — a pattern that used to sit alongside option 3 — in favor of the alternatives above.

---

## What was tried and rejected

Two shapes of "construct a durable adapter directly inside workflow code" were explored and both
were retired before ever shipping as a recommended pattern:

1. **`WorkflowOnlyChatClient.Instance` / `WorkflowOnlyEmbeddingGenerator.Instance` sentinels.**
   `ChatClientBuilder` and `EmbeddingGeneratorBuilder` both require a real inner client instance at
   construction time, but a `[WorkflowRun]` method has no DI container and no real `IChatClient` to
   hand them — workflow code only needs to reach `Workflow.ExecuteActivityAsync`, never the actual
   model. The sentinel types existed purely to satisfy that constructor requirement: a value with
   no behavior of its own, whose only job was to be wrapped by `.UseDurableExecution()` so the
   *outer* `DurableChatClient`/`DurableEmbeddingGenerator` could do the real dispatch.
2. **A later-planned `DurableChatClient.CreateForWorkflow(...)` factory** that would have hidden
   the sentinel behind a cleaner static-factory API surface. This was discussed and evaluated but
   never implemented — grep for it in `src/` or `docs/` today and you will find nothing. It's
   mentioned here only because the two reasons below were surfaced *while evaluating whether to
   build it*, and the reasons apply equally to the sentinel-based version that did briefly exist.

Both are gone — see [Non-breaking note](#non-breaking-note) below for exactly what that means for
anyone who might have been depending on either.

---

## Why it's an anti-pattern

Three findings back this up: constructing the adapter added little real value (a), composing
middleware around it opened a genuine determinism hole (b), and every round of independent review
kept finding new problems rather than converging on "safe" (c). The Quick Answer's ban above covers
both plain construction and middleware composition — (a) and (b) are what it's actually banning;
(c) is the evidence that scrutinizing this surface further wasn't going to fix it.

### (a) Thin ergonomic benefit, not real capability

Look at what `DurableChatClient.GetResponseAsync`'s in-workflow branch actually does
(`src/TemporalCommunity.Extensions.AI/DurableChatClient.cs`): it builds an input DTO, then calls

```csharp
var response = await Workflow.ExecuteActivityAsync(
    (DurableChatActivities a) => a.GetResponseAsync(input),
    CreateActivityOptions(options));
```

That is the exact same primitive a hand-written Activity uses — `Workflow.ExecuteActivityAsync`
against a method that calls the real `IChatClient`. The adapter does not add any capability a
hand-written Activity lacks. What it *does* add over a hand-written Activity is two thin ergonomic
conveniences:

- Reusing `IChatClient.GetResponseAsync(...)` call syntax at the workflow call site, instead of
  defining an Activity class and an input record.
- Auto-translating `ChatOptions` extension methods (`WithActivityTimeout`, etc.) into
  `ActivityOptions` for you.

Meanwhile, the adapter carries real limitations that a hand-written Activity doesn't:

- `ChatOptions.Tools` is rejected outright with `DurableConfigurationException` — no inline tool
  loop is possible; you're pointed at `DurableChatSessionClient`/`AddDurableTools` instead.
- `GetStreamingResponseAsync` throws `NotSupportedException` when invoked in workflow context —
  Temporal activities return a single serialized result, so token-by-token streaming cannot cross
  the workflow/activity boundary.
- `ChatOptions.RawRepresentationFactory` is documented as lost across the activity boundary — it's
  not serializable.
- `DurableChatClient` is a DI singleton shared across every workflow instance on the worker, so
  per-turn metadata like `TurnNumber` silently defaults to `0` on this path — a per-instance
  counter on a shared singleton would aggregate across unrelated sessions and be meaningless.

In short: the same worker-side wiring (a real `IChatClient` registered in DI, `AddDurableAI()`
called) is required either way, and the adapter's benefit is purely syntactic sugar at the
workflow call site — sugar that comes with a growing list of "except when..." caveats.

### (b) A real determinism hazard

This is the crux, and it's not hypothetical — it was traced through decompiled MEAI and Temporal
SDK internals during review. The bottom line up front: composing ordinary MEAI middleware (like a
chat-message reducer) around a direct workflow adapter can execute non-deterministic work inside
workflow code with no guarantee it resumes on Temporal's own scheduler — and the library has no way
to detect or block it. The rest of this section walks through exactly how.

#### Composition order

`ChatClientBuilder.Build()` wraps factories in the *reverse* of their
registration order, so the **first** `.Use(...)` call becomes the **outermost** wrapper around
whatever inner client was passed to the constructor. If you compose

```csharp
new ChatClientBuilder(durableAdapter).UseChatReducer(reducer).Build()
```

the resulting `ReducingChatClient` is unconditionally the outermost layer around the durable
adapter. There's no alternative composition that puts the reducer "inside" the durable boundary —
the durable adapter is fed in as the constructor's inner-client argument, not added via `.Use()`,
so it can only ever end up on the inside.

#### Execution order

MEAI's `ReducingChatClient.GetResponseAsync` runs, unconditionally:

```csharp
messages = await _reducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false);
return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
```

`ReduceAsync` always completes **before** the inner client — the durable adapter, and its
`Workflow.ExecuteActivityAsync` dispatch — is ever invoked. So any reducer composed this way
executes entirely inline in workflow code, ahead of any activity dispatch. The durable adapter has
no way to detect this: its only input validation is `options?.Tools is { Count: > 0 }`, and
whatever `messages` it receives — already reduced by outer middleware or not — is indistinguishable
from a caller who filtered the messages by hand. In short: any middleware stacked around the
durable adapter runs entirely inline in workflow code, invisible to Temporal.

#### Why "the reducer is pure" doesn't make this safe

Temporal's actual execution engine,
`Temporalio.Worker.WorkflowInstance`, **is itself a `TaskScheduler`** (decompiled:
`internal class WorkflowInstance : TaskScheduler`). It drains a single-threaded task queue and this
is the mechanism that makes workflow execution deterministic and replayable. During activation it
explicitly clears the ambient context: `SynchronizationContext.SetSynchronizationContext(null)`. A
bare `await` (default `ConfigureAwait(true)`) correctly resumes back through `WorkflowInstance`'s
scheduler. **`.ConfigureAwait(false)` opts out of that guarantee** — if the awaited work genuinely
suspends, its continuation is not guaranteed to resume via `WorkflowInstance`'s queue at all.

This repo's own `DurableChatClient.GetResponseAsync` already treats this as a real constraint
elsewhere in the same file — recall the `Workflow.ExecuteActivityAsync` call from (a) above; that
exact line deliberately omits `.ConfigureAwait(false)`, with a load-bearing comment explaining why:

> `// Keep this continuation on Temporal's workflow task scheduler so subsequent workflow commands
> are issued through the active workflow context.`

— while every other `await` in that same file (the non-workflow passthrough branches, which never
touch the workflow scheduler) does use `.ConfigureAwait(false)`. In other words: the library
already knows this rule and follows it internally. The direct-adapter pattern let arbitrary
user-composed middleware violate the same rule immediately outside the library's boundary, with no
way for the library to detect or prevent it.

#### A concrete example: `SummarizingChatReducer`

`MessageCountingChatReducer.ReduceAsync` happens to return `Task.FromResult(...)` — an already-completed task, so `.ConfigureAwait(false)`
never actually matters for it, because an `await` on an already-completed task never schedules a
real continuation. That's an implementation accident of one concrete class, not a contract of
`IChatReducer`. MEAI ships a second built-in reducer in the *same assembly*,
`SummarizingChatReducer`, whose `ReduceAsync` makes a genuine network call to an LLM to summarize
older history. Composing `.UseChatReducer(new SummarizingChatReducer(...))` around a direct
workflow adapter would execute that non-deterministic, unrecorded network call directly inside
`[WorkflowRun]`/`[WorkflowUpdate]` code, with no guarantee its continuation resumes on Temporal's
deterministic scheduler at all. `IChatReducer`'s interface contract
(`Task<IEnumerable<ChatMessage>> ReduceAsync(...)`) gives the type system no way to forbid this —
"the reducer looks pure" is not something the compiler, `ChatClientBuilder`, or the library can
verify or enforce.

### (c) Repeated review rounds kept finding new hazards

Every round of independent review of this surface — a DX-focused pass, an architecture-focused
pass, and a separate external opinion — found a **new** hazard rather than converging on "this is
now safe." That repeated pattern (three rounds, three new findings, no round ending in "done") was
treated as evidence that the API shape itself was wrong, not that it needed one more targeted
patch. A surface that keeps surfacing new determinism hazards under scrutiny is not a surface worth
hardening further; it's a surface worth removing.

---

## Why `AIFunction.AsDurable()` is different and was kept

`AIFunction.AsDurable()` was evaluated against the same two concerns above and kept, because its
risk profile is structurally different, not just smaller:

- It's a **single terminal extension method** — `function.AsDurable(options)` returns
  `new DurableAIFunction(function, options)`. It is not a link in a general-purpose
  middleware-composition pipeline the way `.UseDurableExecution()` is one link in
  `ChatClientBuilder`.
- There is no MEAI ecosystem of `AIFunction`-decorating middleware analogous to
  `ChatClientBuilder`'s reducers/loggers/caches that could be stacked *outside* it. The composition
  seam that caused the hazard in (b) above — an outer wrapper whose async work runs inline in
  workflow code, ahead of the durable dispatch — doesn't exist for this primitive, because nothing
  in MEAI composes `AIFunction`s the way `ChatClientBuilder` composes `IChatClient`s.

`DurableAIFunction.InvokeCoreAsync` performs the same "check `Workflow.InWorkflow`, then dispatch
via `Workflow.ExecuteActivityAsync`" shape as the retired chat-client adapter — but with no
reachable seam for arbitrary user code to execute ahead of that dispatch. Same underlying
dispatch mechanism, no equivalent hazard.

---

## The supported patterns

Full detail and code samples live in [MEAI usage — Which pattern should I use?](../../how-to/MEAI/usage.md#which-pattern-should-i-use);
this section is a terse index so you don't have to leave this doc to find your way there.

1. **Managed session** — `DurableChatSessionClient` / `AddDurableAI()`. Default choice when your
   unit of work is "a conversation" and the stock workflow's history/tool-dispatch/continue-as-new
   behavior is exactly what you want.
2. **`DurableChatWorkflowBase<TOutput>` / `DurableToolWorkflowBase<TRequestData, TTurnState>`** —
   sample: `samples/MEAI/CustomWorkflow`. For owning a custom workflow while keeping the package's
   session/history/HITL/continue-as-new machinery. These base classes do **not** use
   `DurableChatClient`/`UseDurableExecution()` internally at all — their turn-dispatch code builds
   `ActivityOptions` directly and calls their own turn activity, so retiring the direct-adapter
   pattern does not affect this tier in any way.
3. **Hand-written Activity + `AIFunction.AsDurable()`** (see Quick Answer above for what this
   covers) — sample: `samples/MEAI/DirectAdapters` (`ResearchActivities.cs` + `ResearchWorkflow.cs`).

---

## Non-breaking note

This was a pre-release course-correction, not a breaking change to any released version.
`WorkflowOnlyChatClient` and `WorkflowOnlyEmbeddingGenerator` never appeared in
`PublicAPI.Shipped.txt` — both were `Unshipped`-only — and `DurableChatClient.CreateForWorkflow(...)`
was discussed but never implemented or shipped. No consumer of a released package version had
anything to migrate away from.

---

## References

- [MEAI usage — Which pattern should I use?](../../how-to/MEAI/usage.md#which-pattern-should-i-use)
- [Durable chat pipeline architecture](durable-chat-pipeline.md)
- [Tool functions how-to](../../how-to/MEAI/tool-functions.md)
- `src/TemporalCommunity.Extensions.AI/DurableChatClient.cs`
- `samples/MEAI/DirectAdapters`
- `samples/MEAI/CustomWorkflow`
