# Individual MAF Context Providers

MAF ships several `AIContextProvider` subclasses as part of its `HarnessAgent` bundle — `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, `AgentSkillsProvider`, and others. The full `HarnessAgent` bundle is structurally incompatible with this library (see [`harness-agent-compatibility.md`](./harness-agent-compatibility.md)), but **most individual session-state providers work today** via `DurableAgentBuilder.AddContextProvider`. Providers that retain live process state (such as `BackgroundAgentsProvider`, which holds `Task<T>` references) are not compatible.

---

## Supported providers

Register a session-state `AIContextProvider` subclass by passing an instance or a DI factory to `agent.AddContextProvider(...)`. MAF's session-state providers are standard subclasses and slot in without modification.

### `TodoProvider`

Gives the agent todo-management tools (`todos_add`, `todos_complete`, `todos_remove`, `todos_get_remaining`, `todos_get_all`) and injects the current todo list as a context message on every LLM step. Useful for long-horizon tasks where the agent needs to track a plan across many turns.

```csharp
opts.AddDurableAgent("PlannerAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(new TodoProvider());
});
```

`TodoProvider` stores state in `AgentSessionStateBag`, which is serialized after every turn and carried forward through continue-as-new transitions — todos survive worker restarts automatically.

### `AgentModeProvider`

Tracks a named operating mode (`"plan"` / `"execute"` by default) in session state and injects mode-specific instructions on every LLM step. The agent can switch modes with the `mode_set` tool; external code can read or write the mode via `provider.GetMode(session)` / `provider.SetMode(session, mode)`.

```csharp
var modeProvider = new AgentModeProvider();

opts.AddDurableAgent("ResearchAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(modeProvider);
});
```

Mode state is stored in `AgentSessionStateBag` and persists across turns and worker restarts. The mode starts as `"plan"` by default; the agent changes it with the `mode_set` tool during the session.

### Combining providers

Multiple providers compose cleanly — each fires in registration order on every LLM step:

```csharp
opts.AddDurableAgent("TaskAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(new AgentModeProvider());
    agent.AddContextProvider(new TodoProvider());
});
```

### `WorkingSetContextProvider`

`WorkingSetContextProvider` is a library-provided `AIContextProvider` that ships with `TemporalCommunity.Extensions.Agents` — no additional package reference required.

On every LLM step it scans the accumulated `ChatMessage` history (assistant and tool messages only), extracts recently-referenced file paths using two heuristics — the first line inside a code fence and path-shaped tokens that contain a `/` or `\` and carry a recognized extension — deduplicates them, and injects a compact `## Working set` system note listing the most-recently-seen files (up to `MaxPaths`, default 20). The extracted paths are also written to `AgentSessionStateBag["temporal.working_set"]` as a CSV string, so the working set survives worker restarts and continue-as-new transitions.

**When to use it.** Agents that read, edit, or reference files across multiple turns — coding assistants, document editors, research agents — benefit from the injected note because it keeps the LLM oriented on which files are active without requiring the user to re-state context.

**Registration.** No DI dependencies; construct directly:

```csharp
opts.AddDurableAgent("CodingAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddContextProvider(new WorkingSetContextProvider());
});
```

**`SilentMode`.** When `SilentMode = true`, the `## Working set` note is suppressed and no tokens are added to the LLM context. The `StateBag` entry is still written, so downstream providers or tools can read the current working set without paying the token cost.

```csharp
agent.AddContextProvider(new WorkingSetContextProvider { SilentMode = true });
```

**Known limitation — external-store sessions.** When `IAgentHistoryStore` is configured, the workflow strips message payloads from in-workflow history entries before passing them to the step activity. `WorkingSetContextProvider` therefore only sees the current turn's messages, not the full accumulated history. For those sessions the injected note (and the `StateBag` entry) reflect only what was mentioned in the current turn. A future revision may close this gap by loading from the store directly.

---

## What is not supported: `BackgroundAgentsProvider`

`BackgroundAgentsProvider` stores live `Task<AgentResponse>` references in its runtime state. Those `Task` objects are in-process handles — they cannot be serialized, cannot survive continue-as-new, and cannot be replayed from workflow history. **Do not register `BackgroundAgentsProvider` with `AddContextProvider`.**

If you need parallel agent fan-out, use `TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` instead — it achieves the same result through `Workflow.WhenAllAsync`, which is fully durable and replay-safe. See [`docs/how-to/MAF/usage.md`](./usage.md) and the [`MultiAgentRouting`](../../../samples/MAF/MultiAgentRouting/) sample.

For a detailed explanation of why the full `HarnessAgent` bundle (which includes `BackgroundAgentsProvider`) is incompatible, see [`harness-agent-compatibility.md`](./harness-agent-compatibility.md).

---

## How provider output is applied

When a registered `AIContextProvider` returns an `AIContext` from `InvokingAsync`, the library applies the result as follows:

> **`AIContext.Instructions`** — applied to the LLM call. The final aggregated `Instructions` value (after all providers in the chain have run) replaces the agent's registered instructions on `ChatOptions.Instructions` for that step. Providers receive the previous provider's `Instructions` output via `InvokingContext`, so each provider in the chain can extend or replace what the prior provider produced.
>
> **`AIContext.Messages`** — merged into the conversation context. The final aggregated `Messages` list becomes the message sequence passed to the LLM for that step. Providers receive the previous provider's `Messages` output via `InvokingContext`, forming a chain.
>
> **`AIContext.Tools`** — **not dispatched**. Provider-contributed tools are explicitly ignored. A `LogWarning` is emitted once per turn (not per provider, not per tool) when any provider returns a non-empty tool set:
>
> ```
> Context provider {ProviderType} returned {ToolCount} tool(s) for agent {AgentName}.
> Provider-contributed tools are not dispatched as durable activities and are ignored.
> Register tools via agent.AddTool() to ensure durable execution.
> ```
>
> This warning fires at runtime and there is no compile-time check. If you implement a custom `AIContextProvider` that returns tools from `InvokingAsync`, register those tools via `agent.AddTool(...)` instead — that is the only path that gives each tool call its own Temporal activity with configurable retry and timeout.

---

## Per-step, not per-turn

Providers fire **once per LLM call**, not once per turn. A single agent turn may involve several LLM calls (one per step in the tool-call loop). Keep provider logic idempotent and cheap:

- Read from `StateBag` rather than making external calls on every step.
- If you must make a network call, cache the result in `StateBag` and skip it on subsequent steps within the same turn.
- The provider instance is constructed once per agent per worker and shared across all sessions — treat instance fields as read-only after construction; all per-session state must live in `StateBag`.

---

## Sample

See [`samples/MAF/ContextProviders/`](../../../samples/MAF/ContextProviders/) for a complete example that demonstrates the `AddContextProvider` pattern with two custom providers — a turn counter and a date/time injector. MAF's own providers (`TodoProvider`, `AgentModeProvider`) follow the same registration pattern and will be demonstrated here once available in the published NuGet package.

See [`samples/MAF/WorkingSet/`](../../../samples/MAF/WorkingSet/) for a focused `WorkingSetContextProvider` demo: a four-turn code assistant that progressively builds a working set from mock file reads, with Turn 4 answered from injected context alone — no tool call required.
