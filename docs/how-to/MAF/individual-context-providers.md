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

The practical reframing: providers do not compose into a pipeline. They run over the same input and
their outputs are concatenated.

**When providers must share, share through the `StateBag`** — one writes a key, the other reads it.
That is what `WorkingSetContextProvider` does, and it is also the only channel that survives a
worker restart.

> If you genuinely need the unfiltered view, MAF lets you override `InvokingCoreAsync` instead of
> `ProvideAIContextAsync`, or pass `provideInputMessageFilter: m => m` to the base constructor. Both
> make you responsible for the merging and source-stamping the default does for you.

### It does not see the agent's registered tools

The context handed to a provider carries `Messages` and `Instructions` only. The agent's durable
tools are attached to the model call separately and never appear in `AIContext`. A provider cannot
check whether `send_email` is registered before mentioning it. `Tools` on the incoming context is
simply always empty.

### Reach session state through `context.Session`

`TemporalAgentContext.Current` is **not** available to a provider. It is established later in the
step, and in the tool activity — not when providers run. Use the session you are handed:

```csharp
if (context.Session is not TemporalAgentSession session)
{
    return new ValueTask<AIContext>(new AIContext());
}

session.StateBag.SetValue("app.tenant", "acme", JsonSerializerOptions.Default);
```

`StateBag` values are **reference types only** — `SetValue<T>`/`TryGetValue<T>` are constrained
`where T : class`, so a counter is stored as a string, not an `int`.

---

## Choosing how to register

**Default: `agent.AddContextProvider(sp => sp.GetRequiredService<MyProvider>())`.** The factory runs
in each step attempt's DI scope, so you get a fresh provider, constructor injection, and
scope-managed disposal.

| You need | Register with | Lifetime | Cost |
|---|---|---|---|
| The common case | `AddContextProvider(sp => sp.GetRequiredService<T>())` | New per activity attempt | None |
| No DI dependencies | `AddContextProvider(sp => new T(...))` | New per activity attempt | The library does not dispose what your factory `new`s |
| The provider declares tools via `IDurableToolSource` | `AddContextProvider(instance)` | **One shared object** | Must be thread-safe; experimental API |
| It contributes tools but you don't own the type | `AddContextProvider(instance, durableTools: [...])` | **One shared object** | Must be thread-safe |

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
disposal (the library disposes neither a supplied instance nor one your factory creates).

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
provider fires on each. A turn that calls three tools runs every provider four times.

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

**`InvokedAsync` fires on failure too.** `InvokedContext` carries an `InvokeException` and has a
dedicated failure shape, so code that records results must handle the failed-invocation case rather
than assuming response messages exist.

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

## `WorkingSetContextProvider`

The one provider that ships with the library. On each LLM step it scans accumulated **assistant and
tool** messages for file paths — from `TextContent`, from `FunctionCallContent` arguments, and from
`FunctionResultContent` results (the latter two only when the value is a string or a JSON string) —
deduplicates case-insensitively, keeps most-recently-seen order, caps at `MaxPaths`, injects a
compact `## Working set` note, and publishes the same list to
`AgentSessionStateBag["temporal.working_set"]` as comma-separated text.

```csharp
agent.AddContextProvider(new WorkingSetContextProvider());
```

| Property | Behaviour |
|---|---|
| `MaxPaths` | Default `20`. `0` disables tracking; a negative value throws `ArgumentOutOfRangeException` rather than silently behaving like `0`. |
| `SilentMode` | Suppresses the injected note while still publishing the `StateBag` entry. |

That key is a **recomputed observational mirror**, not a structured persistence contract: it holds
whatever paths appear in the currently retained history, which is not a judgement that a file is
still relevant. When a step extracts nothing the key is removed rather than left stale.

[`samples/MAF/WorkingSet`](../../../samples/MAF/WorkingSet/) demonstrates the two-provider
`StateBag` handoff — a second provider reads `WorkingSetContextProvider.StateBagKey` rather than
parsing the injected note, which is the pattern to copy.

---

## See also

- [harness-agent-compatibility.md](./harness-agent-compatibility.md) — MAF's built-in providers, in detail
- [skills.md](./skills.md) — `UseSkills`, the durable equivalent of `AgentSkillsProvider`
- [`samples/MAF/ContextProviders`](../../../samples/MAF/ContextProviders/) — minimal `StateBag` read/write
- [`samples/MAF/WorkingSet`](../../../samples/MAF/WorkingSet/) — two-provider `StateBag` handoff
