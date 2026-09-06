# Context Providers: Custom AIContextProvider Subclasses with Durable Agents

## Overview

Shows how to implement and register custom `AIContextProvider` subclasses via `DurableAgentBuilder.AddContextProvider`. Context providers fire once per LLM step (not once per turn), inject additional `ChatMessage` context into each LLM call, and can optionally persist state in `AgentSessionStateBag` so it survives worker restarts and continue-as-new transitions.

This sample demonstrates:
- Implementing `TurnCounterProvider` — a stateful provider that reads and increments a session-scoped counter from `AgentSessionStateBag` and injects it as a system message before each LLM call.
- Implementing `DateTimeProvider` — a stateless provider that injects the current UTC date/time on every step, without touching `AgentSessionStateBag`.
- Registering both providers via `agent.AddContextProvider(...)` — multiple providers compose cleanly in registration order.

MAF's own providers (`TodoProvider`, `AgentModeProvider`) are standard `AIContextProvider` subclasses and register via the same `AddContextProvider` call. See the note in [`individual-context-providers.md`](../../../docs/how-to/MAF/individual-context-providers.md) for details on when they will be demonstrated here.

## Highlights

- **Providers fire per LLM step, not per turn.** A single agent turn may involve multiple LLM calls (one per step in the tool-call loop). Providers must be idempotent and cheap.
- **Stateful providers use `AgentSessionStateBag`.** `TurnCounterProvider` reads and writes `"session.turn_count"` via `TemporalAgentContext.Current`. The StateBag is serialized after every turn and carried forward through continue-as-new transitions and worker restarts — no extra storage needed.
- **Stateless providers need no StateBag.** `DateTimeProvider` computes its value on the fly and returns it directly — showing that `StateBag` is opt-in.
- **`BackgroundAgentsProvider` is not supported.** It holds live `Task<T>` references that cannot survive serialization. Use `WorkflowAgents.ExecuteAgentsInParallelAsync` for parallel agent fan-out instead.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`)
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/ContextProviders
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/ContextProviders
```

### Run

```bash
dotnet run --project samples/MAF/ContextProviders/ContextProviders.csproj
```

### Expected Output

```
Worker started. Sending messages...

Session workflow ID: ta-assistant-<guid>

User : Hello! How many times have you been called so far in this session?
Agent: This is LLM call #1 in this session, so you've reached me once so far!

User : What's the current time?
Agent: The current UTC time is 2026-06-02 14:35.

User : How many total LLM calls have we had now?
Agent: We've had 3 total LLM calls in this session so far.

Done.
```

## Further Reading

- [Individual MAF context providers how-to](../../../docs/how-to/MAF/individual-context-providers.md)
- [Why `HarnessAgent` is incompatible](../../../docs/how-to/MAF/harness-agent-compatibility.md)
- [Usage Guide](../../../docs/how-to/MAF/usage.md)
