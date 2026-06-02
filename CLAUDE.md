# TemporalAgents Project Guide

**Two Temporal .NET SDK integrations for durable AI applications:**
- `Temporalio.Extensions.Agents` — durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`)
- `Temporalio.Extensions.AI` — makes any plain `IChatClient` (MEAI) durable, no Agent Framework required

This document gives load-bearing project context: structure, gotchas, behavioral guarantees. For API how-tos, see `docs/how-to/`.

---

## Quick Facts

- **Language**: C# (.NET 10.0)
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)

---

## Project Structure

```
TemporalAgents/
├── TemporalAgents.slnx        # Solution file (.slnx — use this, not .sln)
├── docs/
│   ├── architecture/          # Internal design docs (durability, sessions, statebag, a2a, pub/sub, etc.)
│   └── how-to/MAF + MEAI/     # Practical guides per library
├── src/
│   ├── Temporalio.Extensions.Agents/   # Agent Framework integration (depends on Extensions.AI)
│   └── Temporalio.Extensions.AI/       # MEAI IChatClient middleware (no Agent Framework)
├── tests/                     # Four projects: {Agents,AI} × {Tests, IntegrationTests}
└── samples/
    ├── MAF/                   # 11 samples: BasicAgent, SplitWorkerClient, WorkflowOrchestration,
    │                          # EvaluatorOptimizer, MultiAgentRouting, HumanInTheLoop,
    │                          # WorkflowRouting, AmbientAgent, ConfigurableAgent,
    │                          # ExternalHistoryStore, PerToolActivities, Compaction
    └── MEAI/                  # 6 samples: DurableChat, DurableTools, OpenTelemetry
                               # (DurableOpenTelemetry.csproj), HumanInTheLoop,
                               # DurableEmbeddings, CustomWorkflow
```

Use `Glob` / `ls` to discover specific files. Notable types and their locations are documented inline elsewhere in this guide (Key Type Locations, JSON Serialization, etc.).

---

## Temporalio.Extensions.AI — Key Concepts

**Entry points** (any of these is sufficient — they produce identical DI state):
- `services.AddHostedTemporalWorker(...).AddDurableAI(opts => ...)` — DI extension (primary)
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new DurableAIPlugin(opts => ...))` — `[Experimental("TAI001")]`

**External usage**: `host.Services.GetRequiredService<DurableChatSessionClient>().ChatAsync(...)` returns `Task<DurableSessionResponse>` (post-Layer-2). `GetHistoryAsync` returns `Task<IReadOnlyList<DurableSessionEntry>>`.

**Required for MEAI types**: `DurableAIDataConverter.Instance` must be set on the Temporal client. Without it, `FunctionCallContent` / `FunctionResultContent` / other `AIContent` subtypes lose `$type` and deserialize as base `AIContent`. **Auto-wired** when using `AddTemporalClient(...)`, `AddHostedTemporalWorker(addr, ns, queue)`, or any of the plugin paths. **Manual `TemporalClient.ConnectAsync` callers** must set it explicitly.

**Per-request overrides** via `ChatOptions` extensions:
- `.WithActivityTimeout(TimeSpan)` / `.WithMaxRetryAttempts(int)` / `.WithHeartbeatTimeout(TimeSpan)` / `.WithChatClientKey(string)`
- Keys are `public const string` constants on `TemporalChatOptionsExtensions`.

**Durable tools**: `AddDurableTools(workerBuilder, params aiFunctions)` registers tools in `DurableFunctionRegistry` (resolved by name in `DurableFunctionActivities`). Or `aiFunction.AsDurable()` wraps as `DurableAIFunction` — passes through when `Workflow.InWorkflow == false`. A per-tool overload — `AddDurableTools(tool, opts => opts.NoRetry().WithTimeout(...))` — accepts a `DurableChatToolOptions` configuration callback that mirrors MAF's `DurableToolOptions` (`StartToCloseTimeout`, `HeartbeatTimeout`, `RetryPolicy` properties + `NoRetry()` / `WithMaxAttempts(int)` / `WithTimeout(TimeSpan)` fluent methods).

**Pattern 3 — Durable Tools in Chat Pipeline (v0.4+)**: Register tools via `AddDurableTools()` **without** `UseFunctionInvocation()`. `DurableChatWorkflow` automatically runs a per-tool dispatch loop: call LLM via `GetChatStepAsync` activity, fan out tool calls in parallel as `InvokeFunctionAsync` activities (via `Workflow.WhenAllAsync`), feed results back to the LLM, loop until `IsFinal` or `MaxToolCallsPerTurn` exceeded. Gives per-tool observability and retry without requiring a custom workflow. Activation is intent-based: `DurableFunctionRegistry.Count > 0` at session start → `DurableChatSessionClient` eagerly resolves per-tool `ActivityOptions` and freezes them in `DurableChatWorkflowInput.ToolActivityOptions` (replay-deterministic). Tool failures default to catch-and-feed-back to LLM (Pattern 1's `FunctionInvokingChatClient` behavior); set `DurableExecutionOptions.MaximumConsecutiveErrorsPerRequest = 0` for MAF-style immediate propagation. `MaxToolCallsPerTurn` (default 20), `MaximumConsecutiveErrorsPerRequest` (default 3), `IncludeDetailedErrors` (default false) all live on `DurableExecutionOptions`. Pattern 3 is exclusive to `DurableChatSessionClient` — middleware (`DurableChatClient`) cannot host a loop; custom workflows use Pattern 2 (`.AsDurable()`). Silent-failure footgun (custom workflow + `AddDurableTools` + no `.AsDurable()`) is caught at runtime by `DurableToolsNotWrappedException` in `GetResponseAsync`. See `docs/how-to/MEAI/tool-functions.md` for Models 1/2/3.

**Context detection**: All middleware (`DurableChatClient`, `DurableAIFunction`, `DurableEmbeddingGenerator`) uses `Workflow.InWorkflow` as the dispatch guard. `false` = pass through; `true` = dispatch as Temporal activity.

**HITL**: see `docs/how-to/MEAI/hitl-patterns.md`. Activity timeout on the underlying `[WorkflowUpdate]` must accommodate human review time.

**Activity summaries** (auto-populated for the Temporal Web UI):
- Chat: `chatOptions.ModelId`
- Tool: function `Name`
- Embedding: `EmbeddingGenerationOptions.ModelId`
- HITL approval is a `[WorkflowUpdate]`, not an activity — no summary site.

**Important notes**:
- `DurableChatActivities` is `internal`; registered as `AddSingletonActivities`. Don't instantiate directly.
- `DurableFunctionRegistry` is internal (`Dictionary<string, AIFunction>`, case-insensitive).
- `IChatClient` must be registered in DI **before** `AddDurableAI` (constructor-injected on activity).
- Use `AddChatClient(innerClient).UseFunctionInvocation().Build()` (idiomatic MEAI DI) over `AddSingleton<IChatClient>`. `UseDurableExecution()` chains onto the same builder.
- `IChatClient` resolution is layered: per-call `ChatOptions.WithChatClientKey("k")` → worker-level `DurableExecutionOptions.DefaultChatClientKey` → unkeyed fallback.

For full API surface, see `docs/how-to/MEAI/usage.md`.

---

## Temporalio.Extensions.Agents — Key Concepts

**Entry points**:
- `services.AddHostedTemporalWorker(...).AddTemporalAgents(opts => opts.AddDurableAgent("Name", a => { a.ChatClient = sp => ...; a.AddTool(...); }))`
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new TemporalAgentsPlugin(opts => ...))` — `[Experimental("TA001")]`. Idempotent if mixed with `AddTemporalAgents()`.

**`AddDurableAgent` is the only registration path in v0.3.** A single fluent `DurableAgentBuilder` consolidates `ChatClient`, tools (with per-tool retry overrides via `DurableToolOptions`), context providers, per-agent timeouts, and external history. DI access happens via per-slot factories on the builder — no `BuildServiceProvider()` bootstrap, no string-keyed dictionaries. Each LLM call dispatches a separate `RunDurableAgentStep` activity; each tool call dispatches a separately named `InvokeAgentTool` activity (per-agent local registry, distinct from MEAI's flat `InvokeFunction`). The library composes the chat pipeline with `UseProvidedChatClientAsIs = true` so users do NOT call `.UseFunctionInvocation()` on their `IChatClient`.

**Configuration**: see `docs/how-to/MAF/usage.md` for the full `TemporalAgentsOptions` reference. Worker-level defaults are prefixed `Default*` (e.g. `DefaultActivityTimeout`, `DefaultHeartbeatTimeout`, `DefaultApprovalTimeout`, `DefaultMaxEntryCount`, `DefaultRetryPolicy`, `DefaultHistoryReducer`, `DefaultTimeToLive`); per-agent overrides on `DurableAgentBuilder` use the unprefixed names. Inheritance rule: `effective = registration.X ?? options.DefaultX`. The worker-level `HistoryStore` factory keeps the unprefixed name (presence is opt-in).

**Two agent types** (use the right one for context):
- `TemporalAIAgent` — workflow-context sub-agent. Access via `TemporalWorkflowExtensions.GetAgent("Name")`.
- `TemporalAIAgentProxy` — external-context proxy. Access via `services.GetTemporalAgentProxy("Name")`.

**Workflow-based routing**: routing belongs inside a `[Workflow]` (durable, replay-cached decisions). Two patterns:
- **Static**: classifier agent → `switch` → hardcoded specialist. Simple, fixed agent set.
- **Dynamic**: an activity calls `TemporalAgentsOptions.GetRegisteredAgentNames()` to discover agents at runtime; the activity's result is cached in workflow history (replay-deterministic). See `samples/MAF/WorkflowRouting/DynamicRoutingWorkflow.cs`.
- `AgentDescriptor` (`Name`, `Description`) lives in `Temporalio.Extensions.Agents.State` — routing activities can build their own description maps locally.
- **Never** call `GetRegisteredAgentNames()` / `IsAgentRegistered()` directly inside a `[Workflow]` — wrap in an activity.

**Parallel agent execution** (workflow-only, uses `Workflow.WhenAllAsync`):
```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (researchAgent, messages, researchSession),
    (summaryAgent,  messages, summarySession),
});
```

**HITL**: see `docs/how-to/MAF/hitl-patterns.md`. Two flows — from inside a tool (activity context) via `TemporalAgentContext.Current.RequestApprovalAsync(...)`; from external systems via `client.GetPendingApprovalAsync` + `SubmitApprovalAsync`. Activity timeout must accommodate human review time.

**StateBag persistence** (`AgentSessionStateBag` for `AIContextProvider` like `Mem0Provider`):
- Serialized after each turn via `session.SerializeStateBag()`
- Stored in `_currentStateBag` on `AgentWorkflow`; passed forward in `AgentWorkflowInput.CarriedStateBag`
- Restored at activity start via `TemporalAgentSession.FromStateBag`
- Empty bag (`StateBag.Count == 0`) returns `null` — no wasted serialization

**External history store** (opt-in for regulated workloads + long sessions): set `opts.HistoryStore = sp => sp.GetRequiredService<MyStore>()` (worker default) or `agent.HistoryStore = sp => ...` (per-agent). When configured, the workflow strips message payloads from in-workflow history entries (`ShouldStripMessagesFromHistoryEntry` returns true), the `RunDurableAgentStep` activity loads prior history from the store on the first step of a turn, and after the turn loop exits the workflow dispatches a separate `AppendAgentTurn` activity that appends the new entries to the store. Complementary to `AIContextProvider`, not a replacement. See `docs/how-to/MAF/external-history-store.md`.

**Per-tool Temporal activities** are the default behavior: every `AddDurableAgent` runs the durable loop (each LLM call is a `RunDurableAgentStep` activity; each tool call is a separately named `InvokeAgentTool` activity). Configure per-tool retry/timeout via the `DurableToolOptions` callback on `agent.AddTool(tool, opts => opts.NoRetry())` — write tools must call `.NoRetry()` (or set `MaximumAttempts = 1`) to prevent double-execution on retry. Cap loop iterations via `agent.MaxToolCallsPerTurn` (default 20). See `docs/how-to/MAF/durable-agents.md`.

**OpenTelemetry**: SDK's `TracingInterceptor` handles Temporal protocol spans; `TemporalAgentTelemetry` handles agent-semantic spans. Composed hierarchy:
```
agent.client.send                     ← TemporalAgentTelemetry
  UpdateWorkflow:RunAgent             ← TracingInterceptor
    RunActivity:ExecuteAgent          ← TracingInterceptor
      agent.turn                      ← TemporalAgentTelemetry (token counts, correlation ID)
```
Register all four sources with the tracer provider. **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic during replay; use `ActivitySourceExtensions.TrackWorkflowDiagnosticActivity` instead.

For full API surface, see `docs/how-to/MAF/usage.md`.

---

## Critical: Durability and Determinism

**MUST READ**: [`docs/architecture/MAF/durability-and-determinism.md`](./docs/architecture/MAF/durability-and-determinism.md)

When a worker crashes:
- ✅ Completed agent calls are **not re-executed** — results replay from history
- ✅ `_currentStateBag` carries forward through `AgentWorkflowInput.CarriedStateBag`
- ✅ Conversation history is serialized in workflow state across continue-as-new transitions

As of Layer 3, `AgentWorkflow : DurableChatWorkflowBase<AgentResponse>`. The shared session loop (history accumulation, mutex, `[WorkflowSignal("Shutdown")]`, `[WorkflowQuery("GetHistory")]`, HITL approval handlers, continue-as-new trigger) lives on the base. `AgentWorkflow` overrides the abstract hooks (`ExecuteTurnAsync`, `BuildResponseEntry`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`) and adds MAF-specific concerns (StateBag carry-forward, `AgentName` search attribute, fire-and-forget signal).

---

## Important Dependencies and Notes

### Microsoft Agent Framework
- `Temporalio.Extensions.Agents` depends on `Temporalio.Extensions.AI` (which transitively brings in MEAI).
- HITL types are MEAI-side: `DurableApprovalRequest` / `DurableApprovalDecision` (from `Temporalio.Extensions.AI`).
- `AgentResponse`, `AIAgent`, `DelegatingAIAgent`, `AgentRunOptions` → `Microsoft.Agents.AI`.
- `ChatClientAgentRunOptions` → `Microsoft.Agents.AI` (not the Hosting package).
- `AgentSessionStateBag.Count` available; `AgentSessionStateBag.Serialize()` uses its own `AgentAbstractionsJsonUtilities.DefaultOptions`.

### Key Type Locations (gotchas)
- `RpcException` — `Temporalio.Exceptions` (NOT `Grpc.Core`)
- `Workflow.CreateContinueAsNewException` — takes `Expression<Func<TWorkflow, Task>>` (no collection expressions inside)
- `WorkflowIdConflictPolicy.UseExisting` — `Temporalio.Api.Enums.V1`
- `IAgentHistoryStore` — `Temporalio.Extensions.Agents.HistoryStore` (opt-in via `opts.HistoryStore` or `agent.HistoryStore`); see `docs/how-to/MAF/external-history-store.md`. **`LoadAsync` takes `bool applyCompaction` (no default value)** — `false` = audit canonical, `true` = projected post-compact view. Erasure path uses `false`; inference + reducer paths use `true`.
- `ICompactionStrategy`, `CompactionContext`, `CompactionResult` — `Temporalio.Extensions.Agents.Compaction` (`[Experimental("TA002")]`). Built-in keys: `"truncation"`, `"sliding-window"`, `"summarization"` pre-registered via `TryAddKeyedSingleton`. See `docs/how-to/MAF/compaction.md`.
- `CompactionMarkerEntry` — `Temporalio.Extensions.AI.Session` (lives in the AI library so both source-gen contexts see the `"compaction-marker"` discriminator). Polymorphic subtype of `DurableSessionEntry`. `CompactedAt` is a `[JsonIgnore]` alias of `CreatedAt` (no wire duplication).
- `CompactionAwareErasureHelper` — `Temporalio.Extensions.Agents.HistoryStore`. Static `EraseSessionDataAsync(store, sessionId, erasedIds)` — only correct GDPR-erasure path when compaction markers may exist.
- `DurableCompactionMarkerException`, `DurableMixedPatternException`, `DurableChatClientFactoryNotFoundException`, `DurableToolsNotWrappedException` — `Temporalio.Extensions.AI.Exceptions`. Marker exception is `[Experimental("TA002")]`; the others are stable.
- `DurableChatStepResult` — `Temporalio.Extensions.AI` (internal sealed) — Pattern 3 activity return type from `GetChatStepAsync`; carries `IsFinal`, `AssistantMessage`, optional `ToolCalls` and `Usage`.
- `DurableChatToolOptions` — `Temporalio.Extensions.AI` (public sealed) — per-tool options builder for Pattern 3; mirrors MAF's `DurableToolOptions` verbatim.

### DI Patterns
- `TemporalAgentsOptions` has an **internal constructor** — always access via the `AddTemporalAgents(opts => ...)` delegate.
- `TryAddSingleton` for `ITemporalAgentClient` — allows custom implementations.
- `ActivatorUtilities.CreateInstance<T>(provider, taskQueue)` — pattern for extra constructor args.

### JSON Serialization (gotchas)
- `AgentSessionJsonContext` (Agents) and `DurableAIJsonContext` (AI) — source-gen contexts for conversation history types.
- `TemporalAgentSession` is **NOT** in any source-gen context. Don't try `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))`.
- `TemporalAgentSession.SerializeStateBag()` delegates to `StateBag.Serialize()`, not session serialization.
- Agents library reuses `DurableAIDataConverter` from the AI library (re-exposed via `TemporalAgentDataConverter`) for chat-content polymorphism.

---

## Testing Gotchas

For full testing patterns, see `docs/how-to/MAF/testing-agents.md` and `docs/how-to/MEAI/testing.md`. Cross-cutting gotchas worth knowing here:

- **`Assert.Throws<T>` requires exact type, not subtype.** Use `Assert.Throws<ArgumentNullException>` for null, not `ArgumentException`. xUnit will fail the test if the thrown exception is a subtype of the expected.
- **Hand-written stubs preferred** over FakeItEasy/Moq in this project. See `StubAIAgent` and `TestChatClient` in the test helpers.
- **Search-attribute pre-registration is conditional.** Agents integration tests only need `TestEnvironmentHelper.StartLocalAsync()` (which pre-registers `AgentName`/`SessionCreatedAt`/`TurnCount`) when `EnableSearchAttributes = true`. Bare `WorkflowEnvironment.StartLocalAsync()` works otherwise. AI integration tests never need pre-registration.
- **Both suites use embedded server** — `WorkflowEnvironment.StartLocalAsync()`. No external `temporal server start-dev` process.

---

## Workflow Best Practices

### ✅ DO
- Use the fluent `.AddTemporalAgents()` builder
- Use `GetAgent()` inside workflows for sub-agent orchestration
- Use `Workflow.UtcNow` and `Workflow.NewGuid()` (not `DateTime.UtcNow` / `Guid.NewGuid()`)
- Set appropriate per-agent TTLs (default: 14 days)
- Validate config eagerly — `string.IsNullOrEmpty` + `InvalidOperationException` for missing config (not `is null` + `ArgumentNullException`)
- Keep OTel spans out of workflows — `agent.turn` lives in `AgentActivities`; `agent.client.send` in `DefaultTemporalAgentClient`
- For non-idempotent write-style tools (send email, write record), pass `opts => opts.NoRetry()` to `agent.AddTool(...)` so the tool's activity does not re-execute on transient failure

### ❌ DON'T
- **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic on replay
- Don't use wall-clock time in workflows (`DateTime.UtcNow`, `DateTimeOffset.Now`)
- Don't use `Random` or `Guid.NewGuid()` in workflows
- Don't call `builder.Build()` twice — assign `var host = builder.Build()` once
- Don't commit real API keys to `appsettings.json` — use `dotnet user-secrets` or environment variables

---

## Build Automation

Build automation uses [`just`](https://just.systems). All recipes in `justfile`. .NET SDK pinned via `global.json` (10.0.x). Versioning via `minver-cli` (local `dotnet tool restore`).

### Core recipes

```bash
just --list             # All recipes
just build              # Restore + Release build (default)
just test-unit-all      # All unit tests — no server required
just test-integration   # Agents integration — embedded server
just test-integration-ai # AI integration — embedded server
just pack               # clean → build → pack → artifacts/packages/*.nupkg
```

### Diagnostic + hang recovery (Tank + Trinity, reviewed by Cypher)

```bash
# When an integration suite hangs and you can't tell which test:
just test-individual tests/Temporalio.Extensions.AI.IntegrationTests       # per-test loop, 180s default cap, reports PASS/FAIL/HANG
just test-individual tests/Temporalio.Extensions.AI.IntegrationTests Pattern3 300  # filter + custom cap

# When a single test command hangs (pipe-buffering hides output):
just test-logged tests/Temporalio.Extensions.Agents.IntegrationTests       # writes to /tmp log, 600s default cap

# Orphaned embedded Temporal servers (.NET SDK extracts a CLI to /var/folders/.../T/):
just list-orphans                                                          # read-only — show, don't kill
just kill-orphans                                                          # narrow — temporal-sdk-dotnet only (safe across projects)
just kill-test-hosts                                                       # opt-in — path-scoped to TemporalAgents (Rider/sibling repos untouched)
just test-clean                                                            # alias: pre-test cleanup

# Worktree cleanup after parallel agent work:
just cleanup-stale-worktrees                                               # SAFE — checks dirty state, single -f only
```

### Sample-canary (verify samples still run end-to-end)

```bash
just test-samples-meai     # 5 MEAI samples, per-sample timeout budget, preflight checks OPENAI_API_KEY + Temporal server
just test-samples-maf      # 10 MAF samples, same
just test-samples          # both
just verify-sample-coverage # drift detector — fails if a new sample dir isn't in the recipe lists
just clean-test-artifacts  # remove artifacts/{test-individual,sample-runs}/
```

**Skipped from sample-canary** (must run manually):
- `samples/{MEAI,MAF}/HumanInTheLoop` — interactive (Console.ReadLine)
- `samples/MAF/SplitWorkerClient` — two processes (Worker + Client)

**Pre-requisites for sample-canary:** GNU coreutils `timeout` (macOS: `brew install coreutils`), `nc` (netcat), `OPENAI_API_KEY` in env or user-secrets, `temporal server start-dev` running on `localhost:7233`. The `_sample-preflight` recipe checks all four and fails with actionable messages.

### Versioning

**Versions** auto-derive from git tags via MinVer: exactly on `X.Y.Z` tag → `X.Y.Z`; N commits after → `X.Y.(Z+1)-preview.N`. Cut a release with `git tag -a X.Y.Z -m "..."` then `just pack`. **Tags must NOT have a `v` prefix** — `Directory.Build.props` does not set `<MinVerTagPrefix>`, so MinVer's default (no prefix) applies. Existing tags follow this convention (`0.1.0`, `0.1.1`, ..., `0.3.0`).

**Publish**: `just publish-nuget` (needs `NUGET_API_KEY`) or `just publish-github` (needs `NUGET_GITHUB_TOKEN`).

---

## CI/CD — GitHub Actions

`.github/workflows/build.yml`. Three jobs: `build` (ubuntu+macOS matrix on push to `main`, runs `just build` + `just test-unit`), `package` (after `build`, `just pack`, uploads artifact), `publish` (`workflow_dispatch` only — pushes pre-built artifact to GitHub or NuGet). Integration tests are excluded from CI.

**Required secrets**: `NUGET_PAT` (GitHub Packages), `NUGET_API_KEY` (NuGet.org).

---

## Run Samples

Prerequisites: `temporal server start-dev` running + `OPENAI_API_KEY` (and optionally `OPENAI_API_BASE_URL`) configured via `dotnet user-secrets` or environment variables.

```bash
# MEAI samples
dotnet run --project samples/MEAI/{DurableChat,DurableTools,HumanInTheLoop,DurableEmbeddings,CustomWorkflow}/...csproj
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj

# MAF samples
dotnet run --project samples/MAF/{BasicAgent,WorkflowOrchestration,EvaluatorOptimizer,MultiAgentRouting,WorkflowRouting,HumanInTheLoop,AmbientAgent,ConfigurableAgent}/...csproj

# Feature-specific demos
dotnet run --project samples/MAF/ExternalHistoryStore/ExternalHistoryStore.csproj   # IAgentHistoryStore + AIContextProvider + reduction strategy
dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj         # per-tool Temporal activities with write-tool no-retry
dotnet run --project samples/MAF/Compaction/Compaction.csproj                       # UseCompaction("summarization") + GDPR erasure cascade demo

# SplitWorkerClient — Worker first, then Client in a separate terminal
dotnet run --project samples/MAF/SplitWorkerClient/Worker/Worker.csproj
dotnet run --project samples/MAF/SplitWorkerClient/Client/Client.csproj
```

---

## Quick Troubleshooting

| Issue | Solution |
|---|---|
| "Cannot find Temporalio package" | Use NuGet, not project refs; `dotnet restore` |
| "Agent not registered" | Verify `.AddTemporalAgents()` includes the agent |
| `InvalidOperationException` from `TemporalAIAgent` (called outside workflow) | `TemporalAIAgent` is workflow-context only. Obtain it via `TemporalWorkflowExtensions.GetAgent` inside a `[Workflow]` method. For external callers, use `services.GetTemporalAgentProxy("Name")` instead. |
| `Assert.Throws<ArgumentException>` fails | xUnit requires exact type — use `ArgumentNullException` for null, `ArgumentException` for empty |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Don't serialize via `DefaultOptions`; use `StateBag.Serialize()` |
| Activity timeout (HITL) | Increase `DefaultActivityTimeout` (or per-agent `ActivityTimeout`) to accommodate human review time |
| OTel spans missing | Register all 4 `ActivitySource` names with the tracer provider |
| Worker won't start | `temporal server start-dev` running on `localhost:7233`? |
| Search attributes missing in UI | `opts.EnableSearchAttributes = true` (opt-in, default `false`); pre-register on production clusters |
| Integration test "Unexpected workflow task failure" | Either set `EnableSearchAttributes = true` AND use `TestEnvironmentHelper.StartLocalAsync()`, or leave search attributes disabled |
| Integration test suite hangs; can't tell which test | `just test-individual <project>` — per-test loop, reports PASS/FAIL/HANG. Default 180s cap, parameterizable. |
| Test command hangs and pipe-buffering hides output | `just test-logged <project>` — writes to `/tmp/temporalagents-test-*.log`; `tail -f` separately. 600s default cap. |
| Orphaned `temporal-sdk-dotnet` processes after `pkill` | `just list-orphans` + `just kill-orphans` — narrow to .NET SDK's extracted binary; safe across projects. |
| Cross-project test hosts being killed | Use `just kill-test-hosts` (path-scoped) not unscoped `pkill`. Documented in `justfile` Process hygiene block. |
| Locked agent worktrees won't remove | `just cleanup-stale-worktrees` — checks dirty state first, single `-f` only. Never use `-f -f` directly. |
| New sample added but `test-samples-*` doesn't pick it up | Hardcoded list is intentional (skips interactive/multi-process). Run `just verify-sample-coverage` to catch drift. |

---

## References

- **Temporal Documentation**: https://docs.temporal.io/
- **Temporal .NET SDK**: https://github.com/temporalio/sdk-dotnet
- **Microsoft Agent Framework**: https://github.com/microsoft/agent-framework

### Temporalio.Extensions.Agents (MAF)

- **Usage Guide**: `docs/how-to/MAF/usage.md`
- **Routing Patterns**: `docs/how-to/MAF/routing.md`
- **Testing Agents**: `docs/how-to/MAF/testing-agents.md`
- **Observability**: `docs/how-to/MAF/observability.md`
- **LLM-Call Interception**: `docs/how-to/MAF/llm-call-interception.md`
- **Scheduling**: `docs/how-to/MAF/scheduling.md`
- **Structured Output**: `docs/how-to/MAF/structured-output.md`
- **HITL Patterns**: `docs/how-to/MAF/hitl-patterns.md`
- **History & Token Optimization**: `docs/how-to/MAF/prompt-caching.md`
- **Durable Agents (per-tool activities)**: `docs/how-to/MAF/durable-agents.md`
- **In-Session Compaction**: `docs/how-to/MAF/compaction.md`
- **Do's and Don'ts**: `docs/how-to/MAF/dos-and-donts.md`
- **Durability Guarantees**: `docs/architecture/MAF/durability-and-determinism.md`
- **Sessions and Workflow Loop**: `docs/architecture/MAF/agent-sessions-and-workflow-loop.md`
- **Pub/Sub Equivalents**: `docs/architecture/MAF/pub-sub-and-event-driven.md`
- **StateBag and AIContextProvider**: `docs/architecture/MAF/session-statebag-and-context-providers.md`
- **Agent-to-Agent Communication**: `docs/architecture/MAF/agent-to-agent-communication.md`

### Temporalio.Extensions.AI (MEAI)

- **Usage Guide**: `docs/how-to/MEAI/usage.md`
- **Tool Functions**: `docs/how-to/MEAI/tool-functions.md` (Model 1 inline / Model 2 custom workflow / Model 3 durable dispatch loop)
- **Embeddings**: `docs/how-to/MEAI/embeddings.md`
- **Testing**: `docs/how-to/MEAI/testing.md`
- **Observability**: `docs/how-to/MEAI/observability.md`
- **HITL Patterns**: `docs/how-to/MEAI/hitl-patterns.md`
- **Custom Workflow Output**: `docs/how-to/MEAI/custom-workflow-output.md`
- **Durable Chat Pipeline**: `docs/architecture/MEAI/durable-chat-pipeline.md`
- **Cross-Library Integration**: `docs/architecture/MEAI/cross-library-integration.md`

