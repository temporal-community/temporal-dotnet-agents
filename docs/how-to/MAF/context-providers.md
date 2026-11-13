# Context providers

MAF's `AIContextProvider` can supply instructions, messages, and tools to an agent turn — the current
date, the calling tenant, a note about which files are in play, or functions the agent can call.
In Temporal managed-agent workflows, executable tools must be registered as durable tools before
the turn; providers can select registered functions but cannot introduce new executable functions at
runtime. The managed session workflow prepares provider context once before the model/tool loop and
reuses that recorded context for each model step in the turn.

**The provider is code. The session is memory.** Your provider object is not where the conversation
lives. With factory registration, it is resolved in activity scopes and may be recreated on a retry
or another worker; instance registration keeps the supplied object shared on that worker. In either
case, provider output and key-level `StateBag` changes cross the workflow/activity boundary as
recorded activity results; they are replayed rather than recomputed after a completed preparation
activity.

Providers contribute **instructions, messages, and tools**. In managed workflows, tool declarations
must use a supported durable registration route; provider selection is limited to the exact function
objects seeded into the `InvokingContext`. The dedicated chat-history memory bridge also supports
MAF's stock on-demand `Search` function. See
[If your provider contributes tools](#if-your-provider-contributes-tools).

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

The default message filter can quietly hide context; tool routing has a different rule and fails
before model dispatch when it is misconfigured.

### It does not see what another provider injected

Providers run in registration order. MAF's default merge combines each provider's instructions,
messages, and tools into the aggregate context; the view *your code* gets is narrower. MAF tags each
message with where it came from, and by default a provider is shown only messages that came from
outside the agent — not ones other providers injected this step. Managed-agent tool restrictions
still apply, as described below.

So if a tenant provider adds "Acme Corp, enterprise plan" as a message, a later provider using the
default `ProvideAIContextAsync` path will not find it in `context.AIContext.Messages`. **The model
sees it. That provider does not.**

The aggregate the library threads from provider to provider does accumulate. `Instructions` are not
filtered, so a later provider *does* see an earlier one's instructions. The default filter narrows
only the message view passed to `ProvideAIContextAsync`.

For same-turn sharing, widen the message filter or override the core hook as needed. For state that
must persist with a Temporal session across turns and worker restarts, use the session `StateBag`.

> Two different escape hatches, often confused:
>
> - `provideInputMessageFilter: m => m` on the base constructor **only widens the view**.
>   `ProvideAIContextAsync` still returns just your addition, and MAF still merges and
>   source-stamps it. This is the one you usually want.
> - Overriding `InvokingCoreAsync` replaces the merge step, so you must return the **full merged
>   context** and stamp your own messages yourself.

### In managed agents, select registered tools rather than inventing functions

MAF's default `ProvideAIContextAsync` merge appends returned tools; it does not filter tools already
in the context. Temporal preparation seeds `AIContext.Tools` with registered durable tools, and the
final selection is frozen for the logical turn. Keep only the exact function objects already seeded
into the provider's `InvokingContext`. Declare tools with `IDurableToolSource` or `durableTools`;
the dedicated chat-history memory registration is the supported route for MAF's on-demand `Search`
function. Provider-created functions, wrappers, and incompatible declarations fail before model
dispatch. See [If your provider contributes tools](#if-your-provider-contributes-tools).

#### Filter tools with `InvokingCoreAsync`

To select a subset, override `InvokingCoreAsync` and return the full context with only the desired
registered tools. The default `ProvideAIContextAsync` path is for adding context, not filtering it:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

internal static class ProviderSelectedTool
{
    internal static void Configure(DurableAgentBuilder agent)
    {
        var searchTool = AIFunctionFactory.Create(
            () => "Search results",
            name: "search");
        agent.AddTool(searchTool);
        agent.AddContextProvider(new SearchToolProvider());
    }

    private sealed class SearchToolProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            var selectedTools = context.AIContext.Tools?
                .Where(tool => tool.Name == "search")
                .ToArray();

            return ValueTask.FromResult(new AIContext
            {
                Instructions = context.AIContext.Instructions,
                Messages = context.AIContext.Messages,
                Tools = selectedTools,
            });
        }
    }
}
```

`AddTool` registers the function with Temporal; the provider returns only the matching function
object seeded into its context. This override owns the merge step, so preserve existing instructions
and messages as shown. If you also add messages, source-stamp them yourself. For the default merge
and filtering behavior, see
[What a provider can and cannot see](#what-a-provider-can-and-cannot-see).

### Reach session state through `context.Session`

Use the session explicitly supplied to the provider rather than depending on ambient
`TemporalAgentContext.Current`:

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
so keep hook work retry-safe. For this DI-resolving pattern, the service registration controls the
resolved provider's lifetime:

```csharp
services.AddScoped<MyProvider>();   // ← without this resolution fails during preparation, before the first model call

// ...
agent.AddContextProvider(sp => sp.GetRequiredService<MyProvider>());
```

Registering it `AddSingleton` instead is legal and gives you the shared-instance semantics described
below — including the concurrency hazard — from a call that reads as if it were turn-local. If you
did not mean that, use `AddScoped`. A factory that directly constructs a provider, such as
`sp => new MyProvider(...)`, controls its own instance creation and the container does not dispose
that instance.

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
overload and **the first preparation activity fails with a non-retryable configuration error,
before any model call**. That is deliberate: silently dropping the tools would leave an agent that
looks configured and never calls them.

### A shared instance is shared across sessions

The instance overload keeps the object you handed it. One provider serves **every session on this
worker and every concurrent activity attempt**. A field like `private string? _tenantId;` will not
throw — it will occasionally serve one customer's context to another, under load, in a way you
cannot reproduce locally.

Treat it as immutable configuration: no mutable fields, no session state, and nothing needing
disposal.

<a id="idurabletoolsource-requires-suppressing-ta001"></a>

### `IDurableToolSource` is experimental

It is marked `[Experimental("TA001")]`. If your project treats warnings as errors, suppress this
diagnostic to implement the interface. This repository treats warnings as errors, so its example
uses:

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
when the turn contains several tool-call iterations. The workflow attempts `InvokedAsync` after the
turn reaches a successful result, failure, or cancellation. Regular workflows invoke managed agents
from an activity; there is no separate workflow-local provider lifecycle.

An activity can be retried before its result is recorded, so either hook can physically execute
more than once for one logical turn. Do not treat the callback count as exactly-once. A completed
preparation result is replayed without rerunning recall. On success, `InvokedAsync` receives the
turn's response messages, including assistant and tool messages; on failure it receives an
`InvokeException`. Cancellation cleanup uses a detached
activity with a short timeout. Its StateBag changes, like other turn state, are rolled back when the
turn fails or is cancelled.

The base `AIContextProvider` skips `StoreAIContextAsync` when `InvokeException` is set. A custom
override of `InvokedCoreAsync` that publishes externally on failure owns that effect. Successful
completion-hook StateBag changes commit with the turn. If one provider's completion hook throws,
its StateBag changes are discarded, the error is logged and contained, and later providers still
run.

**StateBag caching is not side-effect deduplication.** If your provider calls an external service
and the activity fails before recording its result, a retry can call that service again. External
effects and publications are not rolled back; use downstream idempotency or reconciliation where
required.

When a dispatched tool fails, `DurableTurnFailedException` reports bounded per-call outcomes;
timeout or lost completion is **outcome uncertain**, not proof that no effect occurred. If the turn
fails after dispatch, the workflow retains an interrupted-turn history entry even though it rolls
back StateBag changes. Retrying the same request ID replays a retained full outcome, compact record,
or minimal record without dispatching the tool again; see
[request recovery](usage.md#request-id-recovery).

**Keep preparation fast.** It runs in its own activity with the configured per-attempt activity
timeout. Its latency adds to the turn before the first model call; the content it returns counts
toward the model's context window.

**Validate static configuration when constructing the provider**, rather than in every hook.
Factory-registered providers are created on the activity path, and retries can rerun their hooks.

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

**The workflow attempts `InvokedAsync` after failures, but the default skips storage.** A completion
notification carries an `InvokeException` when the turn failed. MAF's default `InvokedCoreAsync`
returns immediately when that exception is non-null, so an override of `StoreAIContextAsync` — the
usual place to record results — is **skipped**. That default is right for most providers. To observe
failures, override `InvokedCoreAsync`; you then own the filtering and success path too. Completion
can itself fail before the hooks run, for example if a provider factory cannot resolve; that failure
is logged and does not turn a failed turn into a successful one.

---

## If your provider contributes tools

The managed session workflow freezes the final provider-selected tool set before the first model
call. A provider may select only the exact function objects seeded into its `InvokingContext`;
object identity is used during preparation to map each object to its registered Temporal route.
The default `ProvideAIContextAsync` path appends returned tools, so filtering requires an
`InvokingCoreAsync` override that returns the full context; see the [selection example](#filter-tools-with-invokingcoreasync).
The seeded functions expose the registered declarations but reject invocation during preparation,
even if a provider catches that rejection. Only a separate, workflow-dispatched tool
activity may invoke the registered implementation. Providers are trusted application code, not a
sandbox; do not capture and invoke separate executable tools or bypass this path with external I/O.
Provider-created closures, wrappers, same-name/schema substitutes, and other
untraceable tools fail non-retryably before model dispatch. The worker validates the frozen
declaration on each activity attempt, and a missing registered route fails as an unregistered tool.
Approval-required registration remains a floor even when a provider selects that tool. The
registered `AgentWorkflow` owns workflow-parked approval; regular workflows should invoke it via
the proxy from an activity.

`AddContextProvider(provider, durableTools: [...])` and `IDurableToolSource` register tools with the
agent; they do not make arbitrary provider-returned functions durable. The provider must still
select the exact function object seeded into its context. MAF's `ChatHistoryMemoryProvider` is
rejected through generic registration because its automatic search and successful-turn storage
require Temporal-specific state and scope validation. Use the dedicated
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

Use the session `StateBag` for provider state that must be carried as part of the Temporal session.

- `AgentWorkflow` carries it across its own continue-as-new automatically.
- A **custom orchestrating workflow** invokes the registered agent proxy through an activity. The
  registered `AgentWorkflow` owns and carries its `StateBag` across its own continue-as-new; the
  calling workflow does not manage that agent state.
- At Continue-as-New, if its serialized size is above 64 KiB, the managed workflow logs a warning;
  it does not trim or reject the state. Measure with
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
