# Context Providers: Individual MAF Providers with Durable Agents

## Overview

Shows how to register individual Microsoft Agent Framework (MAF) context providers — `TodoProvider` and `AgentModeProvider` — via `DurableAgentBuilder.AddContextProvider`. The full MAF `HarnessAgent` bundle is structurally incompatible with this library, but the individual providers are standard `AIContextProvider` subclasses that work today.

This sample demonstrates:
- Registering `TodoProvider` via `agent.AddContextProvider(new TodoProvider())`
- Registering `AgentModeProvider` via `agent.AddContextProvider(new AgentModeProvider())`
- Multiple providers composing cleanly — each fires in registration order per LLM step
- Todo state and agent mode persisting across turns via `AgentSessionStateBag`

## Highlights

- **Providers fire per LLM step, not per turn.** A single agent turn may involve multiple LLM calls. Providers must be idempotent.
- **State lives in `AgentSessionStateBag`.** Both `TodoProvider` and `AgentModeProvider` store per-session state in the `StateBag`, which is serialized after every turn and carried forward through continue-as-new transitions and worker restarts.
- **`BackgroundAgentsProvider` is not supported.** It holds live `Task<T>` references that cannot survive serialization. Use `TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` for parallel agent fan-out instead.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
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

Session workflow ID: ta-planneragent-<guid>

User : I need to write a blog post about Temporal durable agents.
Agent: I've added the following todos to your plan: [research Temporal, outline post, ...] We're currently in plan mode...

User : Go ahead and execute the plan.
Agent: Switching to execute mode. Starting with the first todo...

User : What todos are still open?
Agent: The following todos are still open: [...]

Done.
```

## Further Reading

- [Individual MAF context providers how-to](../../../docs/how-to/MAF/individual-context-providers.md)
- [Why `HarnessAgent` is incompatible](../../../docs/how-to/MAF/harness-agent-compatibility.md)
- [Usage Guide](../../../docs/how-to/MAF/usage.md)
