# Individual MAF Context Providers

MAF ships several `AIContextProvider` subclasses as part of its `HarnessAgent` bundle — `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, `AgentSkillsProvider`, and others. The full `HarnessAgent` bundle is structurally incompatible with this library (see [`harness-agent-compatibility.md`](./harness-agent-compatibility.md)). A provider is supported only when its behavior fits the [bounded durable `ChatClientAgent` contract](../../architecture/MAF/bounded-durable-agent-compatibility.md): it contributes retry-safe instructions/messages, keeps session state in `AgentSessionStateBag`, and declares any tools statically.

---

## Supported provider patterns

Register a compatible `AIContextProvider` subclass by passing an instance or a DI factory to `agent.AddContextProvider(...)`. A factory runs from a fresh activity DI scope on every LLM-step attempt; an instance overload deliberately retains the supplied object. Neither form gives a provider a durable process-local session.

### Harness tool providers are not direct drop-ins

`TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, `AgentSkillsProvider`, CodeAct providers, and similar built-ins expose tools dynamically through `AIContext.Tools` or rely on Harness orchestration. Do not register them unchanged. Dynamic provider tools are dropped by the durable activity and logged as an error; they never become Temporal activities.

If you control the provider, implement `IDurableToolSource` to declare fixed `DurableToolRegistrationSpec` values. If you do not control it, use `AddContextProvider(provider, durableTools)` and provide equivalent explicit `AIFunction` implementations. That is an adapter for static tools—not a way to make an opaque Harness provider or its in-process approval loop durable.

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

**History boundary.** Durable workflows own conversation history. Do not combine this provider with a provider-owned history store; provider-owned external persistence has no atomic idempotent retry contract in this library.

---

## What is not supported: `BackgroundAgentsProvider`

`BackgroundAgentsProvider` stores live `Task<AgentResponse>` references in its runtime state. Those `Task` objects are in-process handles — they cannot be serialized, cannot survive continue-as-new, and cannot be replayed from workflow history. **Do not register `BackgroundAgentsProvider` with `AddContextProvider`.**

If you need parallel agent fan-out, use `WorkflowAgents.ExecuteAgentsInParallelAsync` instead — it achieves the same result through `Workflow.WhenAllAsync`, which is fully durable and replay-safe. See [`docs/how-to/MAF/usage.md`](./usage.md) and the [`MultiAgentRouting`](../../../samples/MAF/MultiAgentRouting/) sample.

For a detailed explanation of why the full `HarnessAgent` bundle (which includes `BackgroundAgentsProvider`) is incompatible, see [`harness-agent-compatibility.md`](./harness-agent-compatibility.md).

---

## How provider output is applied

When a registered `AIContextProvider` returns an `AIContext` from `InvokingAsync`, the library applies the result as follows:

> **`AIContext.Instructions`** — applied to the LLM call. The final aggregated `Instructions` value (after all providers in the chain have run) replaces the agent's registered instructions on `ChatOptions.Instructions` for that step. Providers receive the previous provider's `Instructions` output via `InvokingContext`, so each provider in the chain can extend or replace what the prior provider produced.
>
> **`AIContext.Messages`** — merged into the conversation context. The final aggregated `Messages` list becomes the message sequence passed to the LLM for that step. Providers receive the previous provider's `Messages` output via `InvokingContext`, forming a chain.
>
> **`AIContext.Tools`** — **not dispatched directly**. A `LogError` is emitted once per turn (not per provider, not per tool) when a provider returns tools without a durable registration:
>
> ```
> Context provider {ProviderType} returned {ToolCount} tool(s) for agent {AgentName}.
> Provider-contributed tools are not dispatched as durable activities and are ignored.
> Register tools via agent.AddTool(), `IDurableToolSource`, or AddContextProvider(provider, durableTools) to ensure durable execution.
> ```
>
> This warning fires at runtime and there is no compile-time check. If you implement a custom `AIContextProvider` that returns tools from `InvokingAsync`, register equivalent durable tools with `agent.AddTool(...)`, implement `IDurableToolSource`, or pass explicit specs to `AddContextProvider(provider, durableTools)`. Those are the paths that give each tool call its own Temporal activity with configurable retry and timeout.

---

## Per-step, not per-turn

Providers fire **once per LLM call**, not once per turn. A single agent turn may involve several LLM calls (one per step in the tool-call loop). Keep provider logic idempotent and cheap:

- Read from `StateBag` rather than making external calls on every step.
- If you must make a network call, cache the result in `StateBag` and skip it on subsequent steps within the same turn.
- A factory-created provider can be reconstructed for each activity attempt and can run on another worker. Even a directly supplied instance is not session-owned. All per-session state must live in `StateBag`.

---

## Sample

See [`samples/MAF/ContextProviders/`](../../../samples/MAF/ContextProviders/) for a complete example that demonstrates the compatible `AddContextProvider` pattern with two custom providers — a turn counter and a date/time injector.

See [`samples/MAF/WorkingSet/`](../../../samples/MAF/WorkingSet/) for a focused `WorkingSetContextProvider` demo: a four-turn code assistant that progressively builds a working set from mock file reads, with Turn 4 answered from injected context alone — no tool call required.
