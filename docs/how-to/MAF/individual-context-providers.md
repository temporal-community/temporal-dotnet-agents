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

`WorkingSetContextProvider` is a library-provided `AIContextProvider` that ships with `Temporalio.Extensions.Agents` — no additional package reference required.

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

## Per-step, not per-turn

Providers fire **once per LLM call**, not once per turn. A single agent turn may involve several LLM calls (one per step in the tool-call loop). Keep provider logic idempotent and cheap:

- Read from `StateBag` rather than making external calls on every step.
- If you must make a network call, cache the result in `StateBag` and skip it on subsequent steps within the same turn.
- The provider instance is constructed once per agent per worker and shared across all sessions — treat instance fields as read-only after construction; all per-session state must live in `StateBag`.

---

## Sample

See [`samples/MAF/ContextProviders/`](../../../samples/MAF/ContextProviders/) for a complete example that registers `TodoProvider` and `AgentModeProvider` together and exercises both during a multi-turn conversation.
