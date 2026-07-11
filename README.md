# TemporalAgents

Temporal .NET SDK integrations for building durable AI applications. Two packages, two levels of abstraction:

| Package | Description |
|---------|-------------|
| [`TemporalCommunity.Extensions.AI`](src/TemporalCommunity.Extensions.AI/README.md) | Make any `IChatClient` durable — no Agent Framework required |
| [`TemporalCommunity.Extensions.Agents`](src/TemporalCommunity.Extensions.Agents/README.md) | Durable agent sessions built on Microsoft Agent Framework |

Both packages give AI workloads **durability by default** — conversation history, LLM calls, and tool invocations are persisted in Temporal's event history and replayed deterministically after crashes or restarts.

## Overview

### `TemporalCommunity.Extensions.AI`

A lightweight middleware layer for [Microsoft.Extensions.AI (MEAI)](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). Wraps any `IChatClient` with Temporal durability via a `DelegatingChatClient` middleware. No agent framework, no heavy abstractions — just MEAI pipelines made crash-resilient.

**Start here if:** you are already using MEAI's `IChatClient` directly and want Temporal durability without adopting the full Agent Framework.

```bash
dotnet add package TemporalCommunity.Extensions.AI
```

[Full documentation →](src/TemporalCommunity.Extensions.AI/README.md)

### `TemporalCommunity.Extensions.Agents`

A Temporal integration for [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). Each `AIAgent` session maps to a long-lived Temporal workflow with full session management: history, `StateBag` persistence, HITL approval gates, LLM-powered routing, and parallel agent fan-out.

**Start here if:** you are building with the Microsoft Agent Framework and want durable, stateful, multi-agent sessions.

```bash
dotnet add package TemporalCommunity.Extensions.Agents
```

[Full documentation →](src/TemporalCommunity.Extensions.Agents/README.md)

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

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later to build the repository and run samples
- A running [Temporal server](https://docs.temporal.io/cli#start-dev): `temporal server start-dev`
- An LLM provider (e.g., Azure OpenAI, OpenAI, Ollama)

The published libraries ship `net10.0` and `netstandard2.1` assets. `netstandard2.1` supports
.NET Core 3.1+ and modern .NET; .NET Framework is out of scope. See each package README for
down-level limitations.

## Samples

### `TemporalCommunity.Extensions.AI`

| Sample | Package | Description |
|--------|---------|-------------|
| [DurableChat](samples/MEAI/DurableChat) | `Extensions.AI` | Multi-turn durable chat with `DurableChatSessionClient` and tool functions |
| [DurableTools](samples/MEAI/DurableTools) | `Extensions.AI` | Per-tool activity dispatch with `AsDurable()` and `AddDurableTools` |
| [OpenTelemetry](samples/MEAI/OpenTelemetry) | `Extensions.AI` | OTel tracing — span hierarchy, ActivitySource names, and token attributes |
| [HumanInTheLoop](samples/MEAI/HumanInTheLoop) | `Extensions.AI` | HITL approval gates via `RequestApprovalAsync` and `SubmitApprovalAsync` |
| [DurableEmbeddings](samples/MEAI/DurableEmbeddings) | `Extensions.AI` | `IEmbeddingGenerator` wrapped for durable per-chunk activity dispatch |
| [ToolInterceptor](samples/MEAI/ToolInterceptor) | `Extensions.AI` | Intercept, pause, skip, or block tool calls |
| [CustomWorkflow](samples/MEAI/CustomWorkflow) | `Extensions.AI` | Extend the durable chat workflow with a domain-typed result |

### `TemporalCommunity.Extensions.Agents`

| Sample | Package | Description |
|--------|---------|-------------|
| [BasicAgent](samples/MAF/BasicAgent) | `Extensions.Agents` | External caller pattern — send messages to an agent from a console app |
| [SplitWorkerClient](samples/MAF/SplitWorkerClient) | `Extensions.Agents` | Worker and client in separate processes |
| [WorkflowOrchestration](samples/MAF/WorkflowOrchestration) | `Extensions.Agents` | Sub-agent orchestration inside a Temporal workflow |
| [EvaluatorOptimizer](samples/MAF/EvaluatorOptimizer) | `Extensions.Agents` | Generator + evaluator loop pattern |
| [MultiAgentRouting](samples/MAF/MultiAgentRouting) | `Extensions.Agents` | LLM-powered routing, parallel execution, and OpenTelemetry |
| [HumanInTheLoop](samples/MAF/HumanInTheLoop) | `Extensions.Agents` | HITL approval gates via `[WorkflowUpdate]` |
| [WorkflowRouting](samples/MAF/WorkflowRouting) | `Extensions.Agents` | Static and dynamic durable routing patterns |
| [AmbientAgent](samples/MAF/AmbientAgent) | `Extensions.Agents` | Signal-driven ambient monitoring agent |
| [ConfigurableAgent](samples/MAF/ConfigurableAgent) | `Extensions.Agents` | Per-agent settings and read-only tools |
| [ExternalHistoryStore](samples/MAF/ExternalHistoryStore) | `Extensions.Agents` | External history, context, and reduction |
| [PerToolActivities](samples/MAF/PerToolActivities) | `Extensions.Agents` | Per-tool activity options and no-retry write tools |
| [Compaction](samples/MAF/Compaction) | `Extensions.Agents` | History compaction and GDPR erasure |
| [ContextProviders](samples/MAF/ContextProviders) | `Extensions.Agents` | Custom `AIContextProvider` implementations |
| [DurableContextProvider](samples/MAF/DurableContextProvider) | `Extensions.Agents` | Context-provided tools dispatched durably |
| [MixedActivities](samples/MAF/MixedActivities) | `Extensions.Agents` | Regular and AI activities in one workflow |
| [ApprovalScopes](samples/MAF/ApprovalScopes) | `Extensions.Agents` | Scope-aware approvals persisted across turns |
| [ToolInterceptor](samples/MAF/ToolInterceptor) | `Extensions.Agents` | Pre-tool policy decisions and approval pauses |
| [WorkingSet](samples/MAF/WorkingSet) | `Extensions.Agents` | Working-set file context for coding agents |
| [Skills](samples/MAF/Skills) | `Extensions.Agents` | Progressive-disclosure skill catalog and durable loading |

### Sample credentials

API keys are managed with [dotnet user-secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) — stored outside the repo in `~/.microsoft/usersecrets/` and loaded automatically by `Host.CreateApplicationBuilder()` in the Development environment.

Set `OPENAI_API_KEY` for each sample project you want to run:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
```


Non-sensitive settings (`OPENAI_API_BASE_URL`, `OPENAI_MODEL`, `TEMPORAL_ADDRESS`) have working defaults in each sample's committed `appsettings.json` where applicable, or directly in its code. They do not need user-secrets unless you want to override them.

Alternatively, set `OPENAI_API_KEY` as an environment variable — the samples pick it up automatically via `IConfiguration`.

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
just test         # Unit + integration tests (starts embedded Temporal test servers)
just pack         # Build NuGet packages → artifacts/packages/
just ci           # Full pipeline: clean → build → test-unit → pack
```

## License

[MIT](LICENSE)
