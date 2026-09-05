# TemporalCommunity.Extensions.Agents

A [Temporal](https://temporal.io/) integration for
the [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). This library provides
durable, stateful AI agent sessions backed by Temporal workflows.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later to run the samples below. The
  published package also targets `netstandard2.1` (.NET Core 3.1+ and modern .NET); .NET Framework
  is not supported.
- Temporal Service 1.31.0 or newer ([local development](https://docs.temporal.io/cli#start-dev):
  `temporal server start-dev`)
- An LLM provider (e.g., Azure OpenAI, OpenAI)

Install the NuGet package:

```bash
dotnet add package TemporalCommunity.Extensions.Agents
```

## Getting Started

### 1. Register and start an Agent worker

The following is a complete single-process OpenAI example. Install the provider package and set
`OPENAI_API_KEY` first; substitute another provider if preferred. For a longer runnable example,
see [BasicAgent](../../samples/MAF/BasicAgent).

```bash
dotnet add package Microsoft.Extensions.AI.OpenAI
```

```csharp
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set OPENAI_API_KEY before starting the worker.");
var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));
IChatClient chatClient = openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient();

builder.Services.AddChatClient(chatClient);
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", a =>
        {
            a.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            a.Instructions = "You are a helpful assistant.";
            a.TimeToLive = TimeSpan.FromHours(24);
        });
        // Optional opt-out for clusters where the three custom search attributes
        // have not been registered yet.
        // opts.EnableSearchAttributes = false;
    });

using var host = builder.Build();
await host.StartAsync();
```

### 2. Send a Message

```csharp
// Resolve the agent proxy from the started host.
AIAgent proxy = host.Services.GetTemporalAgentProxy("MyAgent");

// Create a session and send a message
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Text);
```

For the experimental plugin alternative, see `TemporalAgentsPlugin` and
`AddWorkerPlugin()` in the API documentation; keep it out of the first-run path.

### 3. Run a Sample

```bash
# Start Temporal (in a separate terminal)
temporal server start-dev --namespace default \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int

# Run a sample
dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj
```

## Samples

Use the [Sample Catalog](../../samples/catalog.md) to choose a MAF sample by intent and deployment
topology. It is validated against tracked sample projects.

## Documentation

- [Durable approvals](../../docs/concepts/durable-approvals.md) — shared approval lifecycle and the MAF-only scope boundary

### How-To Guides

- [Usage Guide](../../docs/how-to/MAF/usage.md) — structured output, orchestration, HITL, scheduling, OTel, and more
- [Routing Patterns](../../docs/how-to/MAF/routing.md) — static and dynamic workflow-based routing
- [Testing Agents](../../docs/how-to/MAF/testing-agents.md) — unit and integration testing patterns
- [Observability](../../docs/how-to/MAF/observability.md) — OpenTelemetry spans, search attributes, and operational queries
- [LLM-Call Interception](../../docs/how-to/MAF/llm-call-interception.md) — per-LLM-call observability via `ChatClientFactory`
- [Scheduling](../../docs/how-to/MAF/scheduling.md) — recurring and one-time agent runs, lifecycle management
- [Structured Output](../../docs/how-to/MAF/structured-output.md) — typed responses with `RunAsync<T>`, fence stripping, and retry
- [Human-in-the-Loop](../../docs/how-to/MAF/hitl-patterns.md) — approval gates, dashboards, timeouts, and testing
- [History & Token Optimization](../../docs/how-to/MAF/prompt-caching.md) — managing conversation history and reducing costs
- [Do's and Don'ts](../../docs/how-to/MAF/dos-and-donts.md) — common mistakes and best practices
- [Pre-release Worker Cutover](../../docs/how-to/MAF/pre-release-cutover.md) — drain, verify, deploy, and rollback steps for workflow-behavior changes

### Architecture

- [Durability & Determinism](../../docs/architecture/MAF/durability-and-determinism.md) — how replay preserves completed agent calls
- [Agent Sessions & Workflow Loop](../../docs/architecture/MAF/agent-sessions-and-workflow-loop.md) — session lifecycle, message flow, crash recovery
- [Session StateBag & Context Providers](../../docs/architecture/MAF/session-statebag-and-context-providers.md) — AIContextProvider integration and StateBag persistence
- [Bounded Durable `ChatClientAgent` Compatibility](../../docs/architecture/MAF/bounded-durable-agent-compatibility.md) — supported agent/provider inputs and exclusions
- [Pub/Sub & Event-Driven Patterns](../../docs/architecture/MAF/pub-sub-and-event-driven.md) — Temporal equivalents of pub/sub fan-out
- [Agent-to-Agent Communication](../../docs/architecture/MAF/agent-to-agent-communication.md) — sub-agent calls, parallel fan-out, and cross-workflow signaling

### External References

- [Temporal Documentation](https://docs.temporal.io/)
- [Temporal .NET SDK](https://github.com/temporalio/sdk-dotnet)
- [Microsoft Agent Framework](https://github.com/microsoft/agents)

## Overview

Temporal gives AI agents **durability by default** — every agent session maps to a long-lived workflow whose state
survives process crashes and restarts. Conversation history, tool calls, and even human-in-the-loop approval gates are
all persisted in Temporal's event history and replayed deterministically.

Key benefits over in-memory agent frameworks:

- **Request/Response via `[WorkflowUpdate]`** — direct response, no polling
- **Long sessions** — continue-as-new transfers history to fresh runs automatically
- **Observability** — full Temporal Web UI, event history, and distributed tracing
- **Multi-agent orchestration** — first-class workflow fan-out and routing

## Feature Highlights

- Durable multi-turn conversations with automatic history management
- Workflow-based routing — durable, observable, and fully under your control
- Parallel agent execution inside workflows (`ExecuteAgentsInParallelAsync`)
- Human-in-the-loop approval gates via `[WorkflowUpdate]`
- Typed structured output with `RunAsync<T>` (markdown fence stripping + retry)
- Recurring and one-time scheduled agent runs
- MCP tool integration through ordinary `AddTool`/`AddTools` registration; see the
  [MCP guide](../../docs/how-to/MAF/mcp-tools.md)
- External memory with `AIContextProvider` and `AgentSessionStateBag` persistence
- Completed request/response only; `RunStreamingAsync` is not supported
- Pre-tool lifecycle hook via `IAgentToolInterceptor` — intercept, skip, block, or pause for approval before any tool executes; returns `DurableToolDecision` (from `TemporalCommunity.Extensions.AI.Tools`)
- `WorkingSetContextProvider` — `AIContextProvider` subclass that extracts recently-referenced file paths and injects a working-set note before each LLM call
- OpenTelemetry distributed tracing with a stable Temporal `agent.turn` parent and optional
  canonical MAF/MEAI child spans; search attributes enabled by default via `EnableSearchAttributes`
- Plugin composition — `.AddWorkerPlugin()` / `.AddClientPlugin()` available via the `TemporalCommunity.Extensions.AI` dependency (same worker builder, chains after `.AddTemporalAgents()`)

## How It Works

```
External Caller
    │
    │  ExecuteUpdateAsync (RunRequest)
    ▼
AgentWorkflow (long-lived workflow)
    │
    │  ExecuteActivityAsync
    ▼
AgentActivities.RunDurableAgentStepAsync
    │
    ├─► agent.turn (Temporal correlation and fallback usage)
    │     └─► invoke_agent / chat (optional MAF/MEAI telemetry; canonical usage owner)
    └─► Model step and durable tool activities (e.g., ChatClientAgent backed by Azure OpenAI)
```

`agent.turn` is retained when upstream MAF/MEAI telemetry is enabled. A sampled MAF
`invoke_agent` descendant receives the same `temporal.agent.correlation_id`; a standalone MEAI
chat span shares the trace but is created below the library boundary, so correlation is trace-based.
Usage attributes appear on the upstream canonical GenAI span when present and otherwise fall back
to `agent.turn`.

Each agent session maps to a long-lived Temporal **workflow** (`AgentWorkflow`). When an external caller sends a
message, it uses a Temporal **Update** — a durable, acknowledged request/response primitive — to deliver the message and
receive the agent's response in a single call. All AI inference runs inside Temporal **activities**, preserving
determinism.

## Configuration

Key options on `TemporalAgentsOptions` (accessed via the `AddTemporalAgents(opts => ...)` delegate):

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableSearchAttributes` | `bool` | `true` | Upsert `AgentName`, `SessionCreatedAt`, `TurnCount` on each workflow run |
| `DefaultMaxEntryCount` | `int` | `1000` | Cap on `DurableSessionEntry` records (request + response pairs) before triggering continue-as-new |
| `DefaultHistoryReducerKey` | `string?` | `null` | Key of a history reducer registered in DI and executed as an activity at continue-as-new boundaries |
| `DefaultRetryPolicy` | `RetryPolicy?` | `null` | Override the default retry policy for agent activities |
| `DefaultActivityTimeout` | `TimeSpan` | `5 min` | Default start-to-close timeout for agent activities |
| `DefaultApprovalTimeout` | `TimeSpan` | `7 days` | How long a HITL gate waits before auto-rejecting |

`EnableSearchAttributes` defaults to `true`. The three search attributes must be pre-registered on
the Temporal server. Pass them to `temporal server start-dev` as shown above, or run the production
CLI commands in the [Observability guide](../../docs/how-to/MAF/observability.md#search-attributes).
Set the option to `false` to opt out.

`AgentSessionStateBag` is carried across turns and continue-as-new boundaries, but it is not a
general memory store. At continue-as-new time the workflow logs a warning when the serialized bag
exceeds 64 KB; the warning does not trim or fail the session. Keep provider state compact and see
the [context-provider boundary](../../docs/architecture/MAF/bounded-durable-agent-compatibility.md).
Providers and tools can measure the same durable payload representation before a turn is submitted:

```csharp
int carriedBytes = session.StateBag.GetDurableSerializedUtf8ByteCount();
```

An empty bag reports zero because durable agent workflows omit it from the payload.

## Core Components

- **`AgentWorkflow`** — Long-lived workflow with `[WorkflowUpdate]` for request/response
- **`AgentJobWorkflow`** — Fire-and-forget workflow for scheduled and deferred runs
- **`TemporalAIAgent`** — For use inside Temporal workflows (via `GetTemporalAgent`)
- **`TemporalAIAgentProxy`** — For external callers (via `GetTemporalAgentProxy`)
- **`ITemporalAgentClient`** — Update-based client with routing, scheduling, and HITL support
- **`TemporalAgentContext`** — Async-local context for agent tools running inside activities
- **`StructuredOutputExtensions`** — `RunAsync<T>` with markdown fence stripping and retry
- **`IAgentToolInterceptor`** (`TemporalCommunity.Extensions.Agents.Tools`) — pre-tool lifecycle hook; extends `IDurableToolInterceptor<AgentToolContext>` from `TemporalCommunity.Extensions.AI.Tools`; return `DurableToolDecision.Proceed/PauseForApproval/Skip/Block`
- **`WorkingSetContextProvider`** — `AIContextProvider` that injects a compact file-reference note before each LLM call

### Dependency on TemporalCommunity.Extensions.AI

This library depends on `TemporalCommunity.Extensions.AI`. Installing `TemporalCommunity.Extensions.Agents` pulls in `TemporalCommunity.Extensions.AI` automatically — no separate package reference is needed.

The shared HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) are defined in `TemporalCommunity.Extensions.AI.Approvals`. Use the package-specific typed client for one-call decisions. MAF reusable session grants are available only through the explicitly registered `ITemporalAgentApprovalScopeAdministration` service.

`TemporalAgentDataConverter` is auto-wired by `AddTemporalAgents()` for the standard registration
patterns (3-arg `AddHostedTemporalWorker` and `AddTemporalClient`). If an application creates a
client via `TemporalClient.ConnectAsync` and registers it with `AddSingleton<ITemporalClient>`, it
must configure a converter that preserves MAF session entries (normally
`TemporalAgentDataConverter.Instance`). `AddTemporalAgents()` validates that requirement before
the worker starts and fails with configuration guidance when it is not met.

For a custom payload codec, use `TemporalAgentDataConverter.CreateDataConverter(codec)`. Deploy
compatible decoding to every client, worker, replayer, and operational reader before enabling
encoding.

## License

[MIT](../../LICENSE)
