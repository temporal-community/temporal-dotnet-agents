# TemporalAgents

Temporal .NET SDK integrations for building durable AI applications. Two packages, two levels of abstraction:

| Package | Description |
|---------|-------------|
| [`Temporalio.Extensions.AI`](src/Temporalio.Extensions.AI/README.md) | Make any `IChatClient` durable — no Agent Framework required |
| [`Temporalio.Extensions.Agents`](src/Temporalio.Extensions.Agents/README.md) | Durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`) |

Both packages give AI workloads **durability by default** — conversation history, LLM calls, and tool invocations are persisted in Temporal's event history and replayed deterministically after crashes or restarts.

## Overview

### `Temporalio.Extensions.AI`

A lightweight middleware layer for [Microsoft.Extensions.AI (MEAI)](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). Wraps any `IChatClient` with Temporal durability via a `DelegatingChatClient` middleware. No agent framework, no heavy abstractions — just MEAI pipelines made crash-resilient.

**Start here if:** you are already using MEAI's `IChatClient` directly and want Temporal durability without adopting the full Agent Framework.

```bash
dotnet add package Temporalio.Extensions.AI
```

[Full documentation →](src/Temporalio.Extensions.AI/README.md)

### `Temporalio.Extensions.Agents`

A Temporal integration for [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). Each `AIAgent` session maps to a long-lived Temporal workflow with full session management: history, `StateBag` persistence, HITL approval gates, LLM-powered routing, and parallel agent fan-out.

**Start here if:** you are building with the Microsoft Agent Framework and want durable, stateful, multi-agent sessions.

```bash
dotnet add package Temporalio.Extensions.Agents
```

[Full documentation →](src/Temporalio.Extensions.Agents/README.md)

## How It Works

Both packages share the same core pattern: LLM calls run inside Temporal **activities** (never directly in workflows), and conversation turns are delivered via Temporal **Updates** — a durable, acknowledged request/response primitive that eliminates polling.

```
External Caller
    │
    │  WorkflowUpdate (chat turn / agent message)
    ▼
Temporal Workflow  ←── persists history, serializes turns, handles ContinueAsNew
    │
    │  ExecuteActivityAsync
    ▼
Activity  ←── calls real IChatClient / AIAgent — retried automatically on failure
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A running [Temporal server](https://docs.temporal.io/cli#start-dev): `temporal server start-dev`
- An LLM provider (e.g., Azure OpenAI, OpenAI, Ollama)

## Samples

| Sample | Package | Description |
|--------|---------|-------------|
| [DurableChat](samples/MEAI/DurableChat) | `Extensions.AI` | Multi-turn durable chat with `DurableChatSessionClient` and tool functions |
| [DurableTools](samples/MEAI/DurableTools) | `Extensions.AI` | Per-tool activity dispatch with `AsDurable()` and `AddDurableTools` |
| [OpenTelemetry](samples/MEAI/OpenTelemetry) | `Extensions.AI` | OTel tracing — span hierarchy, ActivitySource names, and token attributes |
| [HumanInTheLoop](samples/MEAI/HumanInTheLoop) | `Extensions.AI` | HITL approval gates via `RequestApprovalAsync` and `SubmitApprovalAsync` |
| [DurableEmbeddings](samples/MEAI/DurableEmbeddings) | `Extensions.AI` | `IEmbeddingGenerator` wrapped for durable per-chunk activity dispatch |
| [BasicAgent](samples/MAF/BasicAgent) | `Extensions.Agents` | External caller pattern — send messages to an agent from a console app |
| [SplitWorkerClient](samples/MAF/SplitWorkerClient) | `Extensions.Agents` | Worker and client in separate processes |
| [WorkflowOrchestration](samples/MAF/WorkflowOrchestration) | `Extensions.Agents` | Sub-agent orchestration inside a Temporal workflow |
| [EvaluatorOptimizer](samples/MAF/EvaluatorOptimizer) | `Extensions.Agents` | Generator + evaluator loop pattern |
| [MultiAgentRouting](samples/MAF/MultiAgentRouting) | `Extensions.Agents` | LLM-powered routing, parallel execution, and OpenTelemetry |
| [HumanInTheLoop](samples/MAF/HumanInTheLoop) | `Extensions.Agents` | HITL approval gates via `[WorkflowUpdate]` |

### Sample credentials

Every sample reads credentials from `appsettings.local.json` in its own directory (gitignored). Copy from the committed `appsettings.json` and fill in your values:

```bash
cp samples/MEAI/DurableChat/appsettings.json \
   samples/MEAI/DurableChat/appsettings.local.json
```

```json
{
  "OPENAI_API_KEY": "sk-...",
  "OPENAI_API_BASE_URL": "https://api.openai.com/v1",
  "OPENAI_MODEL": "gpt-4o-mini",
  "TEMPORAL_ADDRESS": "localhost:7233"
}
```

Alternatively, set the values as environment variables — `OPENAI_API_KEY`, `OPENAI_API_BASE_URL`, and `OPENAI_MODEL` — and the samples will pick them up automatically via `IConfiguration`.

```bash
# Start Temporal (separate terminal)
temporal server start-dev --namespace default

# Run a sample
dotnet run --project samples/MEAI/DurableChat
dotnet run --project samples/MAF/BasicAgent
```

## Building

```bash
just build        # Restore + Release build
just test-unit    # Unit tests (no server required)
just test         # Unit + integration tests (requires temporal server start-dev)
just pack         # Build NuGet packages → artifacts/packages/
just ci           # Full pipeline: clean → build → test-unit → pack
```

## License

[MIT](LICENSE)
