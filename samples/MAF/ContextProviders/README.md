# Context Providers: Custom AIContextProvider Subclasses with Durable Agents

## Overview

Shows how to implement and register custom `AIContextProvider` subclasses via `DurableAgentBuilder.AddContextProvider`. In managed session workflows, providers prepare context once per logical turn; the recorded context is reused across model/tool steps. Provider state can be persisted in `AgentSessionStateBag` across worker restarts and continue-as-new transitions.

This sample demonstrates:
- Implementing `TurnCounterProvider` — a stateful provider that reads and increments a session-scoped turn counter from `AgentSessionStateBag` and injects it as a system message before the turn's model/tool loop.
- Implementing `DateTimeProvider` — a stateless provider that injects the current UTC date/time once per turn, without touching `AgentSessionStateBag`.
- Registering both providers via `agent.AddContextProvider(...)` — multiple providers compose cleanly in registration order.

MAF's own built-in providers (`TodoProvider`, `AgentModeProvider`, and the rest) are **not** drop-ins: each contributes tools dynamically through `AIContext.Tools`, which this library does not dispatch. See [`harness-agent-compatibility.md`](../../../docs/how-to/MAF/harness-agent-compatibility.md) for each of them and [`context-providers.md`](../../../docs/how-to/MAF/context-providers.md) for the current provider-tool contract.

## Highlights

- **Provider context is prepared once per managed session turn.** Activity retries can re-run a hook before its result is recorded, so external effects still require idempotency.
- **Stateful providers use `AgentSessionStateBag`.** `TurnCounterProvider` reads and writes `"session.turn_count"` via `context.Session.StateBag` — the durable session carried on the `InvokingContext`. Do **not** reach for `TemporalAgentContext.Current` in a provider: providers run before the activity establishes that context, so it throws. The StateBag is serialized after every turn and carried forward through continue-as-new transitions and worker restarts — no extra storage needed.
- **Stateless providers need no StateBag.** `DateTimeProvider` computes its value on the fly and returns it directly — showing that `StateBag` is opt-in.
- **Model context is bounded separately from Temporal payloads.** The sample configures an estimated 8192-token input limit plus a 1024-token output reserve. Provider-added messages and instructions are checked before the model call; over-budget protected content fails visibly instead of being silently dropped.
- **Failures report recovery identity and call outcomes.** Each turn supplies a request ID. The sample reports typed failures or retained compact/minimal records rather than silently retrying with a new ID. Provider state rolls back on failure, but external effects do not; timeout or lost completion is an uncertain outcome. An application needing recovery after a process crash must persist its request ID before submitting the turn.
- **`BackgroundAgentsProvider` is not supported.** It holds live `Task<T>` references that cannot survive serialization. For parallel agent fan-out, a regular workflow can schedule one activity per registered proxy and await them with `Workflow.WhenAllAsync`; see the [routing guide](../../../docs/how-to/MAF/routing.md#retry-identity-and-parallel-calls).

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

User : How many turns have we had so far in this session?
Agent: This is turn #1 in this session.

User : What's the current time?
Agent: The current UTC time is 2026-06-02 14:35.

User : How many total turns have we had now?
Agent: We've had 3 total turns in this session so far.

Done.
```

## Further Reading

- [Context providers how-to](../../../docs/how-to/MAF/context-providers.md)
- [Why `HarnessAgent` is incompatible](../../../docs/how-to/MAF/harness-agent-compatibility.md)
- [Usage Guide](../../../docs/how-to/MAF/usage.md)
