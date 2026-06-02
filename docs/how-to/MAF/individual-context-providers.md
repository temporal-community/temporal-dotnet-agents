# Individual MAF Context Providers

MAF ships several `AIContextProvider` subclasses as part of its `HarnessAgent` bundle — `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, `AgentSkillsProvider`, and others. The full `HarnessAgent` bundle is structurally incompatible with this library (see [`harness-agent-compatibility.md`](./harness-agent-compatibility.md)), but **the individual providers work today** via `DurableAgentBuilder.AddContextProvider`.

---

## Supported providers

Register any concrete `AIContextProvider` subclass by passing an instance or a DI factory to `agent.AddContextProvider(...)`. MAF's providers are all standard subclasses, so they slot in without modification.

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

To start a session in a specific mode, call `modeProvider.SetMode(null, "execute")` before the first turn (or inject via an `AgentSessionStateBag` on session creation).

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
