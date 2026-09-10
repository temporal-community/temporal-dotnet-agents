# Context providers

An `AIContextProvider` injects instructions or messages into every LLM call an agent makes — the
current date, the calling tenant, a note about which files are in play. You write one class and
register it on the agent; the library runs it before each call.

**The provider is code. The session is memory.** Your provider object is not where the conversation
lives — it is code the library calls again for every LLM call, possibly on a different machine than
the last one. The only thing that travels between those calls is the session's `StateBag`.

Providers contribute **instructions and messages**. They cannot contribute tools — see
[If your provider contributes tools](#if-your-provider-contributes-tools).

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

A runnable version, plus a stateful provider that counts LLM calls in the `StateBag`, is in
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

**When providers must share, share through the `StateBag`** — one writes a key, the other reads it.
That is what `WorkingSetContextProvider` does, and it is also the only channel that survives a
worker restart.

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
registered `AddScoped`.** The library opens a fresh DI scope per `RunDurableAgentStep` attempt and
runs your factory in it. Both halves matter — the factory does not decide the lifetime, your DI
registration does:

```csharp
services.AddScoped<MyProvider>();   // ← without this the resolve throws at the first LLM step

// ...
agent.AddContextProvider(sp => sp.GetRequiredService<MyProvider>());
```

Registering it `AddSingleton` instead is legal and gives you the shared-instance semantics described
below — including the concurrency hazard — from a call that reads as if it were per-attempt. If you
did not mean that, use `AddScoped`.

| You need | Register with | Instances | Cost |
|---|---|---|---|
| The common case | `AddContextProvider(sp => sp.GetRequiredService<T>())` + `AddScoped<T>()` | One per activity attempt | None |
| No DI dependencies | `AddContextProvider(sp => new T(...))` | One per activity attempt | Not disposed — the container did not create it |
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

## Providers run once per LLM step

One turn can involve several LLM calls — one per iteration of the tool-call loop — and every
provider fires on each.

Count the **steps**, not the tools. All tool calls the model asks for in a single response are
dispatched as one batch, and only then does the next LLM call happen. So three tools requested at
once is two steps — providers run twice. Three tools requested one at a time, across three
responses, is four steps.

**`StateBag` caching is not deduplication.** A `StateBag` write becomes durable only when the whole
step succeeds. If your provider calls an external service, writes the result, and the step then
fails, the write is discarded while the external call already happened — and the retry calls it
again. Caching saves tokens and latency; it does not make a side effect happen once. If a side
effect must happen at most once, it belongs in a durable tool with `NoRetry()`, not a provider.

**Keep providers cheap.** They run inside the activity's start-to-close budget, ahead of the model
call. A slow provider eats the timeout the model needs, and the resulting failure looks like a model
timeout.

**Validate configuration in the constructor**, not per step — a provider is built on a hot path.

---

## How your output reaches the model

**`Instructions` concatenate.** The agent's registered instructions come first, then each provider's,
joined with a newline — `"registered-instructions\nprovider-instructions"`. A provider **appends**;
it cannot override the agent's instructions through the default path.

**`Messages` append at the end**, so an injected system note lands *after* the user's most recent
message. That is usually right for a "current state" note and usually wrong for a persona.

**`Tools` are ignored** — see below.

**`InvokedAsync` fires on failure too — but by default nothing of yours runs.** The library notifies
every provider with an `InvokedContext` carrying the `InvokeException`. MAF's default
`InvokedCoreAsync` then returns immediately when that exception is non-null, so an override of
`StoreAIContextAsync` — the usual place to record results — is **skipped**. That default is the
right one for most providers. To actually observe failures, override `InvokedCoreAsync`; you then
own the filtering and the success path too.

One gap to know about: a provider that throws from `InvokingAsync` fails the step *before* the
protected block, so no `InvokedAsync` notification follows for any provider that turn.

---

## If your provider contributes tools

`AIContext.Tools` is **not dispatched**. Tools returned this way never become Temporal activities;
they are dropped and the library logs an error — **once per LLM step**, so a multi-step turn logs it
repeatedly. There is no compile-time check.

Three supported ways to give a provider's tools durable execution:

| Situation | Do this |
|---|---|
| You don't own the provider, or want the stable path | `AddContextProvider(provider, durableTools: [...])` |
| You own it and want the declaration on the type | Implement `IDurableToolSource` (instance overload only; see TA001 above) |
| The tool is unrelated to the provider | Plain `agent.AddTool(...)` |

MAF's own built-in providers — `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`,
`AgentSkillsProvider`, `FileAccessProvider`, `BackgroundAgentsProvider` — all contribute tools
dynamically and are not drop-ins. See
[harness-agent-compatibility.md](./harness-agent-compatibility.md), which covers each of them and
why `BackgroundAgentsProvider` cannot work here at all. For skills specifically, this library ships
`agent.UseSkills(...)` — see [skills.md](./skills.md).

---

## Session state and size

The `StateBag` is the only durable slot a provider has.

- `AgentWorkflow` carries it across its own continue-as-new automatically.
- A **custom orchestrating workflow** using `TemporalAIAgent` must serialize and carry its
  `TemporalAgentSession` when it continues as new. The `StateBag` does not cross an arbitrary
  workflow's boundary by itself.
- It is serialized into activity and workflow payloads. Above 64 KiB the managed workflow logs a
  warning — it does not trim or reject. Measure with
  `stateBag.GetDurableSerializedUtf8ByteCount()`.

**Treat retrieved content as untrusted.** Anything a provider pulls from an external source and
injects — especially as a system message — is a prompt-injection surface. Validate and sanitize it.

---

## The built-in provider

The library ships one provider, `WorkingSetContextProvider`, which keeps a coding-style agent
oriented on which files are in play by deriving them from the conversation and publishing the list
to `AgentSessionStateBag["temporal.working_set"]`.

```csharp
agent.AddContextProvider(new WorkingSetContextProvider());
```

It is also the reference example of the `StateBag` handoff described above: a second provider reads
`WorkingSetContextProvider.StateBagKey` rather than trying to parse the note it injected. See
[working-set.md](./working-set.md).

---

## See also

- [working-set.md](./working-set.md) — the built-in `WorkingSetContextProvider`
- [harness-agent-compatibility.md](./harness-agent-compatibility.md) — MAF's built-in providers, in detail
- [skills.md](./skills.md) — `UseSkills`, the durable equivalent of `AgentSkillsProvider`
- [`samples/MAF/ContextProviders`](../../../samples/MAF/ContextProviders/) — minimal `StateBag` read/write
- [`samples/MAF/WorkingSet`](../../../samples/MAF/WorkingSet/) — two-provider `StateBag` handoff
