# TemporalAgents

Temporal .NET SDK integrations for building durable AI applications. Two packages, two levels of abstraction:

| Package | Description |
|---------|-------------|
| [`TemporalCommunity.Extensions.AI`](src/TemporalCommunity.Extensions.AI/README.md) | Make any `IChatClient` durable — no Agent Framework required |
| [`TemporalCommunity.Extensions.Agents`](src/TemporalCommunity.Extensions.Agents/README.md) | Durable agent sessions built on Microsoft Agent Framework |

Both packages make their supported session and activity boundaries durable. Conversation history and
LLM calls are persisted in Temporal history; registered durable tool invocations are separate
activities and replay safely after crashes or restarts.

## Overview

### `TemporalCommunity.Extensions.AI`

A lightweight integration for [Microsoft.Extensions.AI (MEAI)](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). It provides direct `IChatClient` middleware for custom workflows and `DurableChatSessionClient` for managed multi-turn sessions. Managed sessions own their model/tool loop and use worker-owned default or named toolsets for durable function dispatch; thin clients carry no schemas or implementations. No Agent Framework dependency is required.

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

For help choosing between the two packages, see the [Library Combinations Guide](docs/library-combinations.md).
For the shared approval lifecycle, retry outcomes, and MAF-only scope boundary, see
[Durable approvals](docs/concepts/durable-approvals.md).
For externally reachable session, approval, or tool endpoints, follow the normative
[security boundary](docs/security.md).

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
- Temporal Service 1.31.0 or newer ([local development](https://docs.temporal.io/cli#start-dev):
  `temporal server start-dev`)
- An LLM provider (e.g., Azure OpenAI, OpenAI, Ollama)

The repository is tested against Microsoft.Extensions.AI 10.8.3 and
Microsoft.Agents.AI 1.17.0 and pins embedded tests to Temporal Server 1.31.2. Package consumers
should use those library versions or compatible newer releases and Temporal Service 1.31.0 or
newer.

The published libraries ship `net10.0` and `netstandard2.1` assets. `netstandard2.1` supports
.NET Core 3.1+ and modern .NET; .NET Framework is out of scope. See each package README for
down-level limitations.

## Samples

Use the [Sample Catalog](samples/catalog.md) as the authoritative intent-to-sample index. It records
every tracked sample project, including whether it is currently covered by a local sample-canary recipe.

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
temporal server start-dev --namespace default \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int

# Run a sample
dotnet run --project samples/MEAI/DurableChat
dotnet run --project samples/MAF/BasicAgent
```

## Building

```bash
just build        # Restore + Release build
just test-unit    # Unit tests (no server required)
just test         # Unit + integration tests (starts embedded Temporal test servers)
just benchmark-statebag  # Release-mode StateBag rollback timing and allocation measurements
just pack         # Build NuGet packages → artifacts/packages/
just smoke-extensible-turns # Pack, isolate restore, and run the public typed-turn consumer
just ci           # Full pipeline: clean → build → test-unit → pack
```

## License

[MIT](LICENSE)
