# Context providers

An `AIContextProvider` injects instructions or messages into an agent turn — the current date, the
calling tenant, a note about which files are in play. You write one class and register it on the
agent; the managed session workflow prepares provider context once before the model/tool loop and
reuses that recorded context for each model step in the turn.

**The provider is code. The session is memory.** Your provider object is not where the conversation
lives — it is resolved in activity scopes and may be recreated on another worker. Provider output
and key-level `StateBag` changes cross the workflow/activity boundary as recorded activity results;
they are replayed rather than recomputed after a completed preparation activity.

Providers contribute **instructions and messages**. Generic providers cannot create executable
tools by returning functions; durable tool declarations must use a supported registration route.
The dedicated chat-history memory bridge also supports MAF's stock on-demand `Search` function.
See [If your provider contributes tools](#if-your-provider-contributes-tools).

For the distinction between workflow history, episodic retrieval, semantic facts, and procedural
knowledge, see [Agent memory](memory.md).

---

## Inject context into every LLM call

Subclass `AIContextProvider` and override `ProvideAIContextAsync`. Return only what you are adding:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public sealed class DateTimeProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        new(new AIContext
        {
            Messages =
            [
                new ChatMessage(
                    ChatRole.System,
                    $"[Context] Current UTC time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}"),
            ],
        });
}
```

```csharp
opts.AddDurableAgent("TaskAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(sp => new DateTimeProvider());
});
```

That is the whole path. Two things worth noting from it:

- **Wall-clock time is fine here.** Providers run inside a Temporal *activity* — the unit of
  retryable work Temporal records the result of — not inside the workflow. `DateTimeOffset.UtcNow`
  and `Guid.NewGuid()` are safe. The determinism rules for `[Workflow]` code do not apply.
- **Return only your addition.** The library merges it with what is already there; you never rebuild
  the conversation.

A runnable sample, plus a stateful provider that counts logical turns in the `StateBag`, is in
[`samples/MAF/ContextProviders`](../../../samples/MAF/ContextProviders/).

---

## What a provider can and cannot see

Two limits that produce no error and no log line — just a provider that quietly reads the wrong
thing.

### It does not see what another provider injected

Providers run in registration order, and every provider's output reaches the model. But the view
*your code* gets is narrower. MAF tags each message with where it came from, and by default a
provider is shown only messages that came from outside the agent — not ones other providers
injected this step.

So if a tenant provider adds "Acme Corp, enterprise plan" as a message, a later provider looking for
it in `context.AIContext.Messages` will not find it. **The model sees it. Your other provider does
not.**

The practical reframing: **messages** do not compose into a pipeline. The aggregate the library
threads from provider to provider really does accumulate — and `Instructions` are not filtered, so a
later provider *does* see an earlier one's instructions. It is the message view that is narrowed, by
the default filter, before your override is called.

**When providers must share, share through the `StateBag`** — one writes a key and another reads
it. It is also the only provider-owned channel that survives a worker restart.

> Two different escape hatches, often confused:
>
> - `provideInputMessageFilter: m => m` on the base constructor **only widens the view**.
>   `ProvideAIContextAsync` still returns just your addition, and MAF still merges and
>   source-stamps it. This is the one you usually want.
> - Overriding `InvokingCoreAsync` replaces the merge step, so you must return the **full merged
>   context** and stamp your own messages yourself.

### It does not see the agent's registered tools

The context handed to a provider carries `Messages` and `Instructions` only. The agent's durable
tools are attached to the model call separately and are never seeded into `AIContext`. A provider
cannot check whether `send_email` is registered before mentioning it.

So `Tools` on the incoming context starts empty, and anything you ever find in it was put there by
an earlier provider in the chain — which is the misconfiguration below, not a registered tool.

### Reach session state through `context.Session`

`TemporalAgentContext.Current` is **not** available to a provider. It is established later in the
step, and in the tool activity — not when providers run. Use the session you are handed:

```csharp
context.Session?.StateBag.SetValue("app.tenant", "acme", JsonSerializerOptions.Default);
```

`StateBag` is declared on `AgentSession`, so **no cast to `TemporalAgentSession` is needed** to read
or write it. Cast only when you want something Temporal-specific — the session id, or the durable
history. When you do, do not quietly return an empty context if the cast fails: that is a silent
no-op you will not notice in production. Either fall back to the uncast `StateBag` or throw.

`StateBag` values are **reference types only** — `SetValue<T>`/`TryGetValue<T>` are constrained
`where T : class`. That excludes `int` (store a counter as a string), not collections: `string[]`
and `List<string>` are classes and round-trip as JSON arrays.

---

## Choosing how to register

**Default: `agent.AddContextProvider(sp => sp.GetRequiredService<MyProvider>())`, with the provider
registered `AddScoped`.** The library opens a fresh DI scope for provider preparation and terminal
notification activities, and runs your factory in those scopes. Activity retries can re-run a hook,
so keep hook work retry-safe. Both halves matter — the factory does not decide the lifetime, your
DI registration does:

```csharp
services.AddScoped<MyProvider>();   // ← without this the resolve throws at the first LLM step

// ...
agent.AddContextProvider(sp => sp.GetRequiredService<MyProvider>());
```

Registering it `AddSingleton` instead is legal and gives you the shared-instance semantics described
below — including the concurrency hazard — from a call that reads as if it were turn-local. If you
did not mean that, use `AddScoped`.

| You need | Register with | Instances | Cost |
|---|---|---|---|
| The common case | `AddContextProvider(sp => sp.GetRequiredService<T>())` + `AddScoped<T>()` | One per provider activity attempt | None |
| No DI dependencies | `AddContextProvider(sp => new T(...))` | One per provider activity attempt | Not disposed — the container did not create it |
| The provider declares tools via `IDurableToolSource` | `AddContextProvider(instance)` | **One shared object** | Must be thread-safe; experimental API |
| It contributes tools but you don't own the type | `AddContextProvider(instance, durableTools: [...])` | **One shared object** | Must be thread-safe |

**Disposal follows the same rule.** The scope disposes what the *container* created — so an
`IDisposable` resolved via `AddScoped`/`AddTransient` is cleaned up at the end of the attempt. It
does not dispose an instance you `new`ed inside your own factory, or one you handed to the instance
overload; neither does the library.

### A factory cannot carry tool declarations

Declarations are collected at registration time, when the provider must already exist — a
factory-registered one does not. Register an `IDurableToolSource` provider through the factory
overload and **the first LLM step fails with a non-retryable configuration error**. That is
deliberate: silently dropping the tools would leave an agent that looks configured and never calls
them.

### A shared instance is shared across sessions

The instance overload keeps the object you handed it. One provider serves **every session on this
worker and every concurrent activity attempt**. A field like `private string? _tenantId;` will not
throw — it will occasionally serve one customer's context to another, under load, in a way you
cannot reproduce locally.

Treat it as immutable configuration: no mutable fields, no session state, and nothing needing
disposal.

### `IDurableToolSource` requires suppressing TA001

It is marked `[Experimental("TA001")]`, and experimental diagnostics are **errors**. Implementing it
fails your build with `error TA001 … Suppress this diagnostic to proceed` until you suppress it:

```xml
<NoWarn>$(NoWarn);TA001</NoWarn>
```

The `durableTools:` parameter has no such requirement and reaches the same place. **Prefer
`durableTools:`** unless you need the declaration on the type itself.

---

## Providers run once per logical turn

For managed session workflows, `InvokingAsync` runs in a preparation activity before the model/tool
loop. Its filtered messages, instructions, and StateBag delta are returned to workflow code and
recorded in Temporal history. Every model step in that turn reuses the same prepared context, even
when the turn contains several tool-call iterations. `InvokedAsync` runs once after the turn reaches
a successful result, failure, or cancellation. Regular workflows invoke managed agents from an
activity; there is no separate workflow-local provider lifecycle.

An activity can be retried before its result is recorded, so either hook can physically execute
more than once for one logical turn. Do not treat the callback count as exactly-once. A completed
preparation result is replayed without rerunning recall. `InvokedAsync` receives the final assistant
messages on success and an `InvokeException` on failure. Cancellation cleanup uses a detached
activity with a short timeout. Its StateBag changes, like other turn state, are rolled back when the
turn fails or is cancelled.

The base `AIContextProvider` skips `StoreAIContextAsync` when `InvokeException` is set. A custom
override of `InvokedAsync` that publishes externally on failure owns that effect. Successful
completion-hook StateBag changes commit with the turn. If one provider's completion hook throws,
its StateBag changes are discarded, the error is logged and contained, and later providers still
run.

**StateBag caching is not side-effect deduplication.** If your provider calls an external service
and the activity fails before recording its result, a retry can call that service again. External
effects and publications are not rolled back; use downstream idempotency or reconciliation where
required.

On failure, `DurableTurnFailedException` reports bounded per-call outcomes; timeout or lost
completion is **outcome uncertain**, not proof that no effect occurred. The workflow retains an
interrupted-turn history entry even though it rolls back StateBag changes. Same-request recovery
replays a retained full outcome, compact record, or minimal record without dispatching tools again;
see [request recovery](usage.md#request-id-recovery).

**Keep providers bounded.** Preparation runs in its own activity before the model/tool loop and is
subject to its activity timeout and the model-context budget. A slow provider consumes the turn's
activity budget before the first model step.

**Validate configuration in the constructor**, not per step — a provider is built on a hot path.

---

## How your output reaches the model

**`Instructions` concatenate.** The agent's registered instructions come first, then each provider's,
joined with a newline — `"registered-instructions\nprovider-instructions"`. A provider **appends**;
it cannot override the agent's instructions through the default path.

**`Messages` append at the end**, so an injected system note lands *after* the user's most recent
message. That is usually right for a "current state" note and usually wrong for a persona.

**`Tools` are constrained to supported durable routes** — managed session workflows allow ordinary
providers to select/filter exact registered functions seeded into the input context. The dedicated
chat-history memory registration additionally routes MAF's stock on-demand search function through
a Temporal tool activity. Provider-created functions and wrappers are rejected; see below.

**`InvokedAsync` fires on failure too — but by default nothing of yours runs.** The library notifies
every provider with an `InvokedContext` carrying the `InvokeException`. MAF's default
`InvokedCoreAsync` then returns immediately when that exception is non-null, so an override of
`StoreAIContextAsync` — the usual place to record results — is **skipped**. That default is the
right one for most providers. To actually observe failures, override `InvokedCoreAsync`; you then
own the filtering and the success path too.

If preparation fails, the workflow still attempts the failure completion notification. Completion
itself can fail (for example, if the provider factory cannot resolve); that failure is logged and
does not turn a failed turn into a successful one.

---

## If your provider contributes tools

The managed session workflow freezes the final provider-selected tool set before the first model
call. A provider may return or filter only the exact function objects seeded into its
`InvokingContext`; object identity is used during preparation to map that function to its registered
Temporal route. The seeded functions expose the registered declarations but reject invocation during
preparation, even if a provider catches that rejection. Only a separate, workflow-dispatched tool
activity may invoke the registered implementation. Providers are trusted application code, not a
sandbox; do not capture and invoke separate executable tools or bypass this path with external I/O.
Provider-created closures, wrappers, same-name/schema substitutes, and other
untraceable tools fail non-retryably before model dispatch. The worker validates the frozen
declaration on each activity attempt, and a missing registered route fails as an unregistered tool.
Approval-required registration remains a floor even when a provider selects that tool. The
registered `AgentWorkflow` owns workflow-parked approval; regular workflows should invoke it via
the proxy from an activity.

```csharp
agent.AddTool(searchTool);
```

`AddContextProvider(provider, durableTools: [...])` and `IDurableToolSource` register tools with the
agent; they do not make arbitrary provider-returned functions durable. The provider must still
select the exact registered function instance. MAF's `ChatHistoryMemoryProvider` is rejected through
generic registration because its automatic search and successful-turn storage require
Temporal-specific state and scope validation. Use the dedicated
[chat-history memory registration](memory.md) for this provider; it retains provider state in the
workflow-owned `StateBag`, validates storage/search scope on supported operations, and requires
application authorization at the session-request boundary. Ordinary providers that contribute only
messages or instructions remain supported.
MAF's `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`,
`AgentSkillsProvider`, `FileAccessProvider`, and `BackgroundAgentsProvider` may have other dynamic
tools or specialized state and are not drop-ins. See
[harness-agent-compatibility.md](./harness-agent-compatibility.md).

---

## Session state and size

The `StateBag` is the only durable slot a provider has.

- `AgentWorkflow` carries it across its own continue-as-new automatically.
- A **custom orchestrating workflow** invokes the registered agent proxy through an activity. The
  registered `AgentWorkflow` owns and carries its `StateBag` across its own continue-as-new; the
  calling workflow does not manage that agent state.
- It is serialized into activity and workflow payloads. Above 64 KiB the managed workflow logs a
  warning — it does not trim or reject. Measure with
  `stateBag.GetDurableSerializedUtf8ByteCount()`.

**Treat retrieved content as untrusted.** Anything a provider pulls from an external source and
injects — especially as a system message — is a prompt-injection surface. Validate and sanitize it.

---

## Domain-specific providers

For domain-specific projections, such as file references, case IDs, or documents in scope, write
and register your own provider. If another provider or tool needs the projection, publish a
namespaced value in the `StateBag` rather than parsing injected text.

---

## See also

- [harness-agent-compatibility.md](./harness-agent-compatibility.md) — MAF's built-in providers, in detail
- [`samples/MAF/ContextProviders`](../../../samples/MAF/ContextProviders/) — minimal `StateBag` read/write
