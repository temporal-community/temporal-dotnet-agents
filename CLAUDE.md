# TemporalAgents Project Guide

**A Temporal .NET SDK integration with Microsoft Agent Framework for durable AI agent orchestration.**

This document provides essential context for working with the TemporalAgents codebase. It covers project structure, architecture, key patterns, and important behavioral guarantees.

---

## Quick Facts

- **Language**: C# (.NET 10.0)
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)
- **Status**: Complete — 139 unit tests + 51 integration tests (190 total, all pass)
- **Purpose**: Port of `Microsoft.Agents.AI.DurableTask` pattern to Temporal workflows instead of DurableTask entities
- **Key Pattern**: `[WorkflowUpdate]` replaces Signal+Query+polling for request/response

---

## Project Structure

```
TemporalAgents/
├── CLAUDE.md                               # This file
├── DURABILITY_AND_DETERMINISM.md           # Critical: Durability guarantees after crashes
├── TemporalAgents.slnx                     # Solution file (use this, not .sln)
│
├── src/
│   └── Temporalio.Extensions.Agents/       # Main library
│       ├── ServiceCollectionExtensions.cs  # [LEGACY API] ConfigureTemporalAgents
│       ├── TemporalWorkerBuilderExtensions.cs # [NEW API] .AddTemporalAgents() fluent builder
│       ├── TemporalAgentsOptions.cs        # Configuration (internal ctor)
│       ├── ITemporalAgentClient.cs         # Interface: RunAgentAsync, RouteAsync, HITL
│       ├── DefaultTemporalAgentClient.cs   # Implementation using WorkflowUpdate + OTel
│       ├── AgentWorkflow.cs                # Durable session: history, HITL handlers, StateBag
│       ├── AgentActivities.cs              # Activity: calls real AIAgent, OTel span
│       ├── TemporalAIAgent.cs              # For workflow orchestration (sub-agent)
│       ├── TemporalAIAgentProxy.cs         # For external callers (proxy)
│       ├── TemporalWorkflowExtensions.cs   # GetAgent(), ExecuteAgentsInParallelAsync()
│       ├── AgentWorkflowWrapper.cs         # Wraps agent with request context
│       ├── TemporalAgentSession.cs         # Session with StateBag persistence
│       ├── TemporalAgentTelemetry.cs       # ActivitySource + span/attribute constants
│       ├── IAgentRouter.cs                 # Routing abstraction
│       ├── AIModelAgentRouter.cs               # LLM-backed router implementation
│       ├── AgentDescriptor.cs              # Name+description for routing
│       ├── ApprovalRequest.cs              # HITL: request type
│       ├── ApprovalDecision.cs             # HITL: decision from human
│       ├── ApprovalTicket.cs               # HITL: ticket returned to requester
│       ├── ExecuteAgentResult.cs           # Internal: wraps AgentResponse + StateBag
│       ├── State/                          # Conversation history serialization
│       └── ...
│
├── tests/
│   ├── Temporalio.Extensions.Agents.Tests/       # 139 unit tests
│   │   ├── TemporalWorkerBuilderExtensionsTests.cs
│   │   ├── AIModelAgentRouterTests.cs
│   │   ├── RoutingOptionsTests.cs
│   │   ├── HITLTypesTests.cs
│   │   ├── StateBagPersistenceTests.cs
│   │   ├── TemporalAgentTelemetryTests.cs
│   │   ├── TemporalWorkflowExtensionsTests.cs
│   │   ├── Helpers/
│   │   │   ├── StubAIAgent.cs              # Test double: implements CreateSessionCoreAsync
│   │   │   └── CapturingChatClient.cs      # Test double: records ChatOptions
│   │   └── ...
│   │
│   └── Temporalio.Extensions.Agents.IntegrationTests/ # 51 integration tests
│       └── (use real Temporal server)
│
└── samples/
    ├── BasicAgent/                         # External caller pattern (legacy API)
    ├── SplitWorkerClient/                  # Worker + Client in separate processes
    ├── WorkflowOrchestration/              # Workflow sub-agent pattern (new API)
    ├── EvaluatorOptimizer/                 # Generator+Evaluator loop pattern
    ├── MultiAgentRouting/                  # Routing + parallel execution + OTel
    └── HumanInTheLoop/                     # HITL approval gates via WorkflowUpdate
```

---

## Key Concepts

### 1. Two Registration APIs

#### Configure Agents
```csharp
services.ConfigureTemporalAgents(
    configure: opts => opts.AddAIAgent(agent),
    taskQueue: "agents",
    targetHost: "localhost:7233",
    @namespace: "default");
```

#### ✅ NEW API (Fluent Builder - Recommended)
```csharp
services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts => opts.AddAIAgent(agent));
```

The new API composes with other worker configuration (e.g., `.ConfigureOptions(opts => opts.MaxConcurrentActivities = 20)`). The old API delegates to it internally.

---

### 2. Two Agent Types

#### `TemporalAIAgent` (Workflow Context)
- **Use Case**: Inside a Temporal workflow calling a sub-agent
- **Access**: Via `TemporalWorkflowExtensions.GetAgent("AgentName")`

#### `TemporalAIAgentProxy` (External Context)
- **Use Case**: External caller (API server, CLI, console app)
- **Access**: Via `services.GetTemporalAgentProxy("AgentName")`

---

### 3. LLM-Powered Routing

```csharp
builder.Services.AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent);
        opts.AddAIAgent(billingAgent);
        opts.AddAgentDescriptor("WeatherAgent", "Handles weather questions.");
        opts.AddAgentDescriptor("BillingAgent", "Handles billing inquiries.");
        opts.SetRouterAgent(routerAgent);  // registers AIModelAgentRouter as IAgentRouter
    });

// External routing — LLM picks the specialist automatically
var response = await client.RouteAsync(sessionKey, new RunRequest(userMessage));
```

- `SetRouterAgent` registers `AIModelAgentRouter` as `IAgentRouter` in DI automatically
- `AIModelAgentRouter` uses exact match then fuzzy (case-insensitive) fallback on the response text
- Throws `InvalidOperationException` if the LLM returns an unrecognized agent name

---

### 4. Parallel Agent Execution

Only valid **inside a `[Workflow]`** — uses `Workflow.WhenAllAsync` internally:

```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (researchAgent, messages, researchSession),
    (summaryAgent,  messages, summarySession),
});
// IReadOnlyList<AgentResponse> in input order
```

---

### 5. Human-in-the-Loop (HITL)

From inside an **agent tool** (running inside an activity):
```csharp
var ticket = await TemporalAgentContext.Current.RequestApprovalAsync(
    new ApprovalRequest { Action = "Delete records", Details = "Irreversible." });
if (!ticket.Approved) throw new OperationCanceledException("Rejected.");
```

From an **external system** (e.g., an admin dashboard):
```csharp
var pending = await client.GetPendingApprovalAsync(sessionId);
var ticket  = await client.SubmitApprovalAsync(sessionId,
    new ApprovalDecision { RequestId = pending!.RequestId, Approved = true });
```

The workflow blocks on `WaitConditionAsync` during approval — the activity timeout on `RequestApprovalAsync` must be long enough to accommodate human review time.

---

### 6. StateBag Persistence

`AgentSessionStateBag` (used by AIContextProviders like `Mem0Provider`) is now persisted across turns:
- `AgentActivities.ExecuteAgentAsync` serializes the bag after each turn via `session.SerializeStateBag()`
- `AgentWorkflow` stores it in `_currentStateBag` and passes it forward in `ExecuteAgentInput`
- `TemporalAgentSession.FromStateBag` restores it at the start of each activity
- An **empty** bag returns `null` (checked via `StateBag.Count == 0`) — no wasted serialization

---

### 7. OpenTelemetry

The SDK's `TracingInterceptor` handles Temporal protocol spans; `TemporalAgentTelemetry` handles agent-semantic spans. They compose:

```
agent.client.send                     ← TemporalAgentTelemetry (agent name, session ID)
  UpdateWorkflow:RunAgent             ← TracingInterceptor SDK span
    RunActivity:ExecuteAgent          ← TracingInterceptor SDK span
      agent.turn                      ← TemporalAgentTelemetry (token counts, correlation ID)
```

Register **all four** sources:
```csharp
Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,
        TracingInterceptor.WorkflowsSource.Name,
        TracingInterceptor.ActivitiesSource.Name,
        TemporalAgentTelemetry.ActivitySourceName)  // "Temporalio.Extensions.Agents"
    .AddOtlpExporter()
    .Build();
```

**⚠️ Never** use `ActivitySource.StartActivity()` inside a `[Workflow]` class — use `ActivitySourceExtensions.TrackWorkflowDiagnosticActivity` instead (only needed for custom workflow spans; agent spans are in activities/client code).

---

## Critical: Durability and Determinism

**MUST READ**: [`DURABILITY_AND_DETERMINISM.md`](./DURABILITY_AND_DETERMINISM.md)

When a worker crashes:
- ✅ Completed agent calls are **not re-executed** — results are replayed from history
- ✅ `_currentStateBag` is carried forward through `AgentWorkflowInput.CarriedStateBag`
- ✅ Conversation history is serialized in workflow state across continue-as-new transitions

---

## Important Dependencies and Notes

### Temporal .NET SDK
- **Use NuGet packages** (`Temporalio 1.11.1`, `Temporalio.Extensions.Hosting 1.11.1`), NOT project references
- **Reason**: Rust native bridge (`sdk-core-c-bridge`) requires Rust toolchain to build from source
- **OTel extension**: `Temporalio.Extensions.OpenTelemetry 1.11.1` — matches SDK version

### Microsoft Agent Framework
- `AgentResponse`, `AIAgent`, `DelegatingAIAgent`, `AgentRunOptions` → `Microsoft.Agents.AI`
- `ChatClientAgentRunOptions` → `Microsoft.Agents.AI` (not the Hosting package)
- `AgentSessionStateBag.Count` — available, used to detect empty bag without serializing
- `AgentSessionStateBag.Serialize()` — uses its own `AgentAbstractionsJsonUtilities.DefaultOptions`

### MEAI v10 Breaking Changes
- `IChatClient.CompleteAsync` → `GetResponseAsync` (returns `Task<ChatResponse>`)
- `ChatCompletion` → `ChatResponse`
- `StreamingChatCompletionUpdate` → `ChatResponseUpdate`

### Key Type Locations
- `RpcException` — `Temporalio.Exceptions` (not Grpc.Core)
- `Workflow.CreateContinueAsNewException` — takes `Expression<Func<TWorkflow, Task>>` (no collection expressions inside)
- `WorkflowIdConflictPolicy.UseExisting` — `Temporalio.Api.Enums.V1`

### DI Patterns
- `TemporalAgentsOptions` — **internal constructor** (always access via delegate parameter)
- `IAgentRouter` — registered automatically as singleton when `SetRouterAgent` is called
- `TryAddSingleton` for `ITemporalAgentClient` — allows custom implementations
- `ActivatorUtilities.CreateInstance<T>(provider, taskQueue)` — pattern for extra constructor args

### JSON Serialization
- `TemporalAgentStateJsonContext` — source-generated context for conversation history types only
- `TemporalAgentSession` is **NOT** in the source-gen context — do not try to serialize it via `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))` directly
- `TemporalAgentSession.SerializeStateBag()` — delegates to `StateBag.Serialize()`, not session serialization

---

## Testing Patterns

### Unit Tests (139 total)
- **Framework**: xunit with `[Fact]` attributes
- **Assertions**: `Assert.*` — `Assert.Throws<T>` requires **exact** type, not subtype (use `Assert.Throws<ArgumentNullException>` for null, not `ArgumentException`)
- **Mocking**: Hand-written fakes/stubs preferred over Moq
- `StubAIAgent` — implements `CreateSessionCoreAsync` returning `new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey(Name ?? "stub"))`

### Integration Tests (51 total)
- Require real Temporal server (`temporal server start-dev`)
- Location: `tests/Temporalio.Extensions.Agents.IntegrationTests/`

### InternalsVisibleTo
- Via MSBuild: `<InternalsVisibleTo Include="TestProject" />` in `.csproj`
- Internal types accessible in tests: `ExecuteAgentResult`, `ExecuteAgentInput`

---

## Workflow Best Practices

### ✅ DO

- **Use fluent API** — `.AddTemporalAgents()` instead of `ConfigureTemporalAgents()`
- **Use `GetAgent()`** — inside workflows for sub-agent orchestration
- **Use `Workflow.UtcNow`** — not `DateTime.UtcNow`
- **Use `Workflow.NewGuid()`** — not `Guid.NewGuid()` inside workflows
- **Set appropriate TTLs** — `timeToLive` per agent (default: 14 days)
- **Validate config eagerly** — use `string.IsNullOrEmpty` + `InvalidOperationException` for missing config values (not `is null` + `ArgumentNullException`)
- **Keep OTel spans out of workflows** — `agent.turn` is in `AgentActivities`, `agent.client.send` is in `DefaultTemporalAgentClient` — both are correct

### ❌ DON'T

- **Don't call `ActivitySource.StartActivity()` inside `[Workflow]`** — non-deterministic during replay
- **Don't use wall-clock time in workflows** — `DateTime.UtcNow`, `DateTimeOffset.Now`
- **Don't use `Random` or `Guid.NewGuid()` in workflows** — non-deterministic
- **Don't call `builder.Build()` twice** — assign `var host = builder.Build()` and keep the reference
- **Don't commit real API keys in `appsettings.json`** — use `appsettings.local.json` (gitignored) or environment variables

---

## Common Patterns

### Pattern 1: External Agent Call
```csharp
var proxy = services.GetTemporalAgentProxy("MyAgent");
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync(userMessage, session);
```

### Pattern 2: LLM-Powered Routing
```csharp
var response = await agentClient.RouteAsync(sessionKey, new RunRequest(userMessage));
```

### Pattern 3: Parallel Fan-out (inside workflow)
```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (agentA, messages, sessionA),
    (agentB, messages, sessionB),
});
```

### Pattern 4: HITL Approval (inside a tool)
```csharp
var ticket = await TemporalAgentContext.Current.RequestApprovalAsync(
    new ApprovalRequest { Action = "Deploy to production" });
```

### Pattern 5: Workflow Sub-Agent
```csharp
[WorkflowRun]
public async Task<string> RunAsync(string request)
{
    var agent = TemporalWorkflowExtensions.GetAgent("SubAgent");
    var session = await agent.CreateSessionAsync();
    return (await agent.RunAsync(request, session)).Text ?? string.Empty;
}
```

---

## Build Automation

Build automation uses [`just`](https://just.systems) (a `make`-like command runner). All common tasks are recipes in `justfile`. The .NET SDK version is pinned via `global.json` (10.0.x). Package versioning uses `minver-cli` as a local dotnet tool (`.config/dotnet-tools.json`).

### Prerequisites

```bash
# Install just (macOS)
brew install just

# Install minver-cli and any other local dotnet tools
dotnet tool restore
```

### Build

```bash
just build        # Restore + Release build (default)
just build-debug  # Restore + Debug build
just restore      # Restore packages only
just info         # Show solution, version, config, artifacts path
```

### Testing

```bash
just test-unit          # 139 unit tests — no server required
just test-integration   # 51 integration tests — requires: temporal server start-dev
just test               # Both suites (unit + integration)

just test-coverage      # Unit tests with XPlat Code Coverage (output: artifacts/packages/coverage/)
just test-filter "FullyQualifiedName~Router"  # Run tests matching a filter expression
```

> Integration tests require a running Temporal server:
> ```bash
> temporal server start-dev --namespace default
> ```

### Packaging

```bash
just pack   # clean → build → pack → artifacts/packages/*.nupkg + *.snupkg
```

Packages land in `artifacts/packages/`. The version is computed automatically from the nearest git tag by MinVer:

| Git state | Example version |
|-----------|----------------|
| Exactly on `v1.0.0` tag | `1.0.0` |
| 3 commits after `v1.0.0` | `1.0.1-preview.3` |
| No tags in repo | `0.0.0-preview.{height}` |

To cut a release: `git tag -a v1.0.0 -m "Release 1.0.0"` then `just pack`.

### Publishing

```bash
# Publish to NuGet.org (requires NUGET_API_KEY env var)
just publish-nuget

# Publish to GitHub Packages (requires NUGET_GITHUB_TOKEN env var)
just publish-github
```

### Full CI pipeline (local)

```bash
just ci   # clean → build → test-unit → pack
```

Mirrors what GitHub Actions runs. Use this before pushing to verify the full pipeline locally.

### All recipes

```bash
just --list   # Print all available recipes with descriptions
```

---

## CI/CD — GitHub Actions

Pipeline defined in `.github/workflows/build.yml`. Three jobs:

| Job | Runs on | Triggered by |
|-----|---------|-------------|
| `build` | ubuntu + macOS matrix | every push to `main` |
| `package` | ubuntu | after `build` succeeds |
| `publish` | ubuntu | `workflow_dispatch` on `main` only |

**`build` job**: `dotnet tool restore` → `just build` → `just test-unit`
(Integration tests are excluded from CI — they require a live Temporal server.)

**`package` job**: full git history checkout (`fetch-depth: 0`, required for MinVer) → `just pack` → uploads `.nupkg` + `.snupkg` as a workflow artifact named `packages`.

**`publish` job**: downloads the pre-built artifact (no recompilation) → pushes to the registry selected via the `workflow_dispatch` dropdown (`GitHub` or `NuGet`).

### Required GitHub Secrets

| Secret | Used by |
|--------|---------|
| `NUGET_PAT` | Publish to GitHub Package Registry |
| `NUGET_API_KEY` | Publish to NuGet.org |

---

## Run Samples

```bash
# All samples require: temporal server start-dev + OPENAI_API_KEY in appsettings.json

dotnet run --project samples/BasicAgent/BasicAgent.csproj
dotnet run --project samples/WorkflowOrchestration/WorkflowOrchestration.csproj
dotnet run --project samples/EvaluatorOptimizer/EvaluatorOptimizer.csproj
dotnet run --project samples/MultiAgentRouting/MultiAgentRouting.csproj
dotnet run --project samples/HumanInTheLoop/HumanInTheLoop.csproj

# SplitWorkerClient — run Worker first, then Client in a separate terminal
dotnet run --project samples/SplitWorkerClient/Worker/Worker.csproj
dotnet run --project samples/SplitWorkerClient/Client/Client.csproj
```

---

## Architecture Diagram (Extended)

```
┌────────────────────────────────────────────────────────────────┐
│                    External Caller / Client                    │
└──────────┬──────────────────┬────────────────────┬────────────┘
           │                  │                    │
           │ GetTemporalAgent  │ ITemporalAgent      │ ITemporalAgent
           │ Proxy(name)       │ Client.RouteAsync   │ Client.SubmitApproval
           ▼                  ▼                    ▼
  ┌──────────────┐   ┌──────────────────┐   ┌──────────────────┐
  │ Temporal     │   │ DefaultTemporal  │   │ DefaultTemporal  │
  │ AIAgentProxy │   │ AgentClient      │   │ AgentClient      │
  └──────┬───────┘   │ + IAgentRouter   │   │ + HITL support   │
         │           └────────┬─────────┘   └────────┬─────────┘
         │                    │                      │
         └───────────────┬────┘                      │
                         │ ExecuteUpdateAsync         │
                         ▼                            ▼
              ┌──────────────────────────────────────────────────┐
              │                  AgentWorkflow                   │
              │  • conversation history (_history)               │
              │  • StateBag (_currentStateBag)                   │
              │  • HITL state (_pendingApproval)                 │
              │  • RunAgentAsync [WorkflowUpdate]                │
              │  • RequestApprovalAsync [WorkflowUpdate]         │
              │  • SubmitApprovalAsync [WorkflowUpdate]          │
              └──────────┬───────────────────────────────────────┘
                         │ ExecuteActivityAsync
                         ▼
              ┌──────────────────────────────────────────────────┐
              │          AgentActivities.ExecuteAgentAsync       │
              │  • restores StateBag from input                  │
              │  • emits agent.turn OTel span (token counts)     │
              │  • calls real AIAgent (ChatClientAgent)          │
              │  • serializes updated StateBag into result       │
              └──────────────────────────────────────────────────┘
```

---

## Quick Troubleshooting

| Issue | Solution |
|-------|----------|
| "Cannot find Temporalio package" | Use NuGet, not project refs; run `dotnet restore` |
| "Agent not registered" | Verify agent is added via `.AddTemporalAgents()` |
| "Router returned unrecognized name" | Check `AddAgentDescriptor` names match registered agents exactly |
| `Assert.Throws<ArgumentException>` fails | xUnit requires exact type — use `ArgumentNullException` for null, `ArgumentException` for empty |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Do not serialize `TemporalAgentSession` via `DefaultOptions`; use `StateBag.Serialize()` directly |
| "Activity timeout" | Increase `ActivityStartToCloseTimeout` — especially for HITL (needs human review time) |
| OTel spans missing | Ensure all 4 `ActivitySource` names are registered with the tracer provider |
| "Worker won't start" | Verify `temporal server start-dev` is running on `localhost:7233` |

---

## References

- **Temporal Documentation**: https://docs.temporal.io/
- **Temporal .NET SDK**: https://github.com/temporalio/sdk-dotnet
- **Microsoft Agent Framework**: https://github.com/microsoft/agents
- **Durability Guarantees**: `DURABILITY_AND_DETERMINISM.md`
- **Sessions and Workflow Loop**: `docs/AGENT_SESSIONS_AND_WORKFLOW_LOOP.md`
- **Pub/Sub Equivalents**: `docs/PUB_SUB_AND_EVENT_DRIVEN.md`
- **StateBag and AIContextProvider**: `docs/SESSION_STATEBAG_AND_CONTEXT_PROVIDERS.md`

---

**Last Updated**: 2026-02-28
