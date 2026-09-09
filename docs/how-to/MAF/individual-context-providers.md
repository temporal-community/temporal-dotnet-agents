# Context providers

An `AIContextProvider` injects instructions or messages before each LLM call. This library supports
them — with one structural constraint: **a provider may not contribute tools dynamically.**

A provider fits the [bounded durable `ChatClientAgent` contract](../../architecture/MAF/bounded-durable-agent-compatibility.md)
when it contributes retry-safe instructions and messages, keeps its session state in
`AgentSessionStateBag`, and declares any tools statically.

---

## Registering a provider

Pass an instance or a DI factory to `agent.AddContextProvider(...)`:

```csharp
opts.AddDurableAgent("TaskAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

    agent.AddContextProvider(new WorkingSetContextProvider());          // instance
    agent.AddContextProvider(sp => new TenantProvider(sp.GetRequiredService<ITenantLookup>()));  // factory
});
```

Providers fire in registration order on every LLM step, each receiving the previous provider's
output through `InvokingContext` — so a chain can extend or replace what came before.

A factory is rebuilt from a fresh activity DI scope on every step attempt. The instance overload
retains the object you supplied. **Neither gives the provider a durable, process-local session** —
per-session state must live in the `StateBag`.

---

## The one hard limit: provider-contributed tools

`AIContext.Tools` is **not dispatched**. Tools returned this way never become Temporal activities;
they are dropped, and the library logs one error per turn (not per provider, not per tool):

```
Context provider {ProviderType} returned {ToolCount} tool(s) for agent {AgentName}.
Provider-contributed tools are not dispatched as durable activities and are ignored.
Register tools via agent.AddTool(), `IDurableToolSource`, or AddContextProvider(provider, durableTools) to ensure durable execution.
```

There is no compile-time check — this surfaces at runtime only. Three supported ways to give a
provider's tools durable execution:

| Situation | Do this |
|---|---|
| You own the provider | Implement `IDurableToolSource`, declaring fixed `DurableToolRegistrationSpec` values |
| You don't own it | `AddContextProvider(provider, durableTools)` with equivalent explicit `AIFunction`s |
| The tool is unrelated to the provider | Plain `agent.AddTool(...)` |

That second row is an adapter for *static* tools. It is not a way to make an opaque provider or its
in-process approval loop durable.

### MAF's Harness providers are not drop-ins

`TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, `AgentSkillsProvider`, the CodeAct
providers, and similar built-ins ship as part of MAF's `HarnessAgent` bundle. They expose tools
dynamically through `AIContext.Tools` or depend on Harness orchestration, so registering them
unchanged hits exactly the limit above. See
[`harness-agent-compatibility.md`](./harness-agent-compatibility.md) for why the bundle as a whole
does not fit.

### `BackgroundAgentsProvider` cannot work here at all

It stores live `Task<AgentResponse>` handles in its runtime state. Those are in-process objects:
they cannot be serialized, cannot survive continue-as-new, and cannot replay from history. **Do not
register it.**

For parallel agent fan-out use `WorkflowAgents.ExecuteAgentsInParallelAsync`, which reaches the same
result through `Workflow.WhenAllAsync` and is fully replay-safe. See [usage.md](./usage.md) and the
[`MultiAgentRouting`](../../../samples/MAF/MultiAgentRouting/) sample.

---

## How provider output is applied

**`Instructions`** — the final aggregated value **replaces** `ChatOptions.Instructions` for that
step. The agent's own registered instructions seed the chain, so providers extend or override them
rather than being appended to them. This matches MAF's own `ChatClientAgent` behaviour.

**`Messages`** — the final aggregated list becomes the message sequence sent to the LLM.

**`Tools`** — ignored, as above. Providers implementing `IDurableToolSource` have their tools
stripped from the aggregate right after they run, so a downstream provider's `InvokingContext` stays
clean and the error above cannot fire against the wrong provider.

---

## Providers run per step, not per turn

One turn can involve several LLM calls — one per iteration of the tool-call loop — and every
provider fires on each. Keep provider logic idempotent and cheap:

- Read from `StateBag` instead of making an external call on every step.
- If a network call is unavoidable, cache the result in `StateBag` and skip it on later steps in
  the same turn.
- Assume the provider may be reconstructed on a different worker between steps. All per-session
  state belongs in `StateBag`.

---

## `WorkingSetContextProvider`

Ships with `TemporalCommunity.Extensions.Agents` — no extra package reference. It keeps a coding-
style agent oriented on which files are currently in play, without the user re-stating them.

On each LLM step it scans accumulated **assistant and tool** messages and extracts file paths from:

- `TextContent` — the first line inside a code fence, and path-shaped tokens (containing `/` or `\`
  **and** carrying a recognized extension)
- `FunctionCallContent` arguments — the higher-signal source, since this is a path the model
  explicitly named
- `FunctionResultContent` results

Paths are deduplicated case-insensitively, kept in most-recently-seen order, and capped at
`MaxPaths`. It then injects a compact `## Working set` system note and writes the same paths to
`AgentSessionStateBag["temporal.working_set"]` as a comma-separated string, so the working set
survives worker restarts and continue-as-new.

```csharp
agent.AddContextProvider(new WorkingSetContextProvider());
```

| Property | Behaviour |
|---|---|
| `MaxPaths` | Default `20`. Most-recent paths win when the window overflows. `0` disables tracking; a negative value throws `ArgumentOutOfRangeException` rather than silently behaving like `0`. |
| `SilentMode` | Suppresses the injected note — zero added tokens — while still writing the `StateBag` entry for downstream providers and tools. |

The `StateBag` key mirrors the **current** working set: when a step extracts nothing, the key is
removed rather than left holding a stale list. A reader can therefore trust that what is there is
in scope.

**History boundary.** Durable workflows own conversation history. Do not pair this with a
provider-owned history store — provider-owned external persistence has no atomic idempotent retry
contract in this library.

---

## Samples

| Sample | Shows |
|---|---|
| [`samples/MAF/ContextProviders/`](../../../samples/MAF/ContextProviders/) | Two custom providers — a turn counter reading and writing `StateBag`, and a date/time injector |
| [`samples/MAF/WorkingSet/`](../../../samples/MAF/WorkingSet/) | A four-turn code assistant that builds a working set from mock file reads, with the last turn answered from injected context alone |
