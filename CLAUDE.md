# TemporalAgents Project Guide

**Two Temporal .NET SDK integrations for durable AI applications:**
- `TemporalCommunity.Extensions.Agents` — durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`)
- `TemporalCommunity.Extensions.AI` — makes any plain `IChatClient` (MEAI) durable, no Agent Framework required

This document gives load-bearing project context: structure, gotchas, behavioral guarantees. For API how-tos, see `docs/how-to/`.

---

## Quick Facts

- **Development target**: C# (.NET 10.0). The two published libraries also ship
  `netstandard2.1` assets; samples and tests remain `net10.0`.
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)
- **Minimum Temporal Service**: 1.31.0. Embedded tests pin Temporal CLI `v1.8.0` and verify its
  Server 1.31.2 through `GetSystemInfo`; Temporalio NuGet packages use 1.17.0.

---

## Project Structure

```
TemporalAgents/
├── TemporalAgents.slnx        # Solution file (.slnx — use this, not .sln)
├── docs/
│   ├── architecture/          # Internal design docs (durability, sessions, statebag, a2a, pub/sub, etc.)
│   └── how-to/MAF + MEAI/     # Practical guides per library
├── src/
│   ├── TemporalCommunity.Extensions.Agents/   # Agent Framework integration (depends on Extensions.AI)
│   └── TemporalCommunity.Extensions.AI/       # MEAI IChatClient middleware (no Agent Framework)
├── tests/                     # Four projects: {Agents,AI} × {Tests, IntegrationTests}
└── samples/
    ├── MAF/                   # 19 samples — run: ls samples/MAF
    └── MEAI/                  # 7 samples  — run: ls samples/MEAI
```

Use `Glob` / `ls` to discover specific files. Notable types and their locations are documented inline elsewhere in this guide (Key Type Locations, JSON Serialization, etc.).

---

## TemporalCommunity.Extensions.AI — Key Concepts

**Entry points** (any of these is sufficient — they produce identical DI state):
- `services.AddHostedTemporalWorker(...).AddDurableAI(opts => ...)` — DI extension (primary)
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new DurableAIPlugin(opts => ...))` — `[Experimental("TAI001")]`

**External usage**: `host.Services.GetRequiredService<DurableChatSessionClient>().SendAsync(...)` returns `Task<DurableSessionResponse>` (post-Layer-2). `GetHistoryAsync` returns `Task<IReadOnlyList<DurableSessionEntry>>`.

**Required for MEAI types**: `DurableAIDataConverter.Instance` must be set on the Temporal client. Without it, `FunctionCallContent` / `FunctionResultContent` / other `AIContent` subtypes lose `$type` and deserialize as base `AIContent`. **Auto-wired** when using `AddTemporalClient(...)`, `AddHostedTemporalWorker(addr, ns, queue)`, or any of the plugin paths. **Manual `TemporalClient.ConnectAsync` callers** must set it explicitly.

**Per-request overrides** via `ChatOptions` extensions:
- `.WithActivityTimeout(TimeSpan)` / `.WithMaxRetryAttempts(int)` / `.WithHeartbeatTimeout(TimeSpan)` / `.WithChatClientKey(string)`
- Keys are `public const string` constants on `TemporalChatOptionsExtensions`.

**Durable tools**: `AddDurableTools(workerBuilder, params aiFunctions)` registers functions for managed durable sessions; each model-requested call becomes an `InvokeFunction` activity. A per-tool overload — `AddDurableTool(tool, opts => opts.NoRetry().WithTimeout(...))` — accepts a `DurableChatToolOptions` configuration callback that mirrors MAF's `DurableToolOptions` (`StartToCloseTimeout`, `HeartbeatTimeout`, `RetryPolicy` properties + `NoRetry()` / `WithMaxAttempts(int)` / `WithTimeout(TimeSpan)` fluent methods). `AIFunction.AsDurable()` remains the separate adapter for direct calls from custom workflow code. Managed sessions reject caller-supplied `ChatOptions.Tools` and inline `UseFunctionInvocation()` loops; see `docs/how-to/MEAI/tool-functions.md`.

`AIFunction.AsDurable()` activities intentionally use the calling workflow's task queue. They do
not use `DurableExecutionOptions.TaskQueue`; that property routes managed sessions and direct
chat/embedding adapters.

**HITL**: see `docs/how-to/MEAI/hitl-patterns.md`. Activity timeout on the underlying `[WorkflowUpdate]` must accommodate human review time.

**Important notes**:
- `DurableChatActivities` is `internal`; registered as `AddSingletonActivities`. Don't instantiate directly.
- `DurableFunctionRegistry` is internal (`Dictionary<string, AIFunction>`, case-insensitive).
- `IChatClient` must be registered in DI **before** `AddDurableAI` (constructor-injected on activity).
- Use `AddChatClient(innerClient).UseFunctionInvocation().Build()` (idiomatic MEAI DI) over `AddSingleton<IChatClient>`. `UseDurableExecution()` chains onto the same builder.
- `IChatClient` resolution is layered: per-call `ChatOptions.WithChatClientKey("k")` → worker-level `DurableExecutionOptions.DefaultChatClientKey` → unkeyed fallback.
- **Options naming asymmetry** (intentional — do not rename): `DurableExecutionOptions` properties use unprefixed names (e.g. `ActivityTimeout`, `HeartbeatTimeout`, `RetryPolicy`). `TemporalAgentsOptions` worker-level defaults use the `Default*` prefix (e.g. `DefaultActivityTimeout`, `DefaultHeartbeatTimeout`, `DefaultRetryPolicy`). The prefix distinguishes worker-level defaults from per-agent overrides on `DurableAgentBuilder`, which use the unprefixed names.

For full API surface, see `docs/how-to/MEAI/usage.md`.

---

## TemporalCommunity.Extensions.Agents — Key Concepts

**Entry points**:
- `services.AddHostedTemporalWorker(...).AddTemporalAgents(opts => opts.AddDurableAgent("Name", a => { a.ChatClient = sp => ...; a.AddTool(...); }))`
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new TemporalAgentsPlugin(opts => ...))` — `[Experimental("TA001")]`. Idempotent if mixed with `AddTemporalAgents()`.

**`AddDurableAgent` is the only registration path in v0.3.** A single fluent `DurableAgentBuilder` consolidates `ChatClient`, tools (with per-tool retry overrides via `DurableToolOptions`), context providers, per-agent timeouts, and external history. DI access happens via per-slot factories on the builder — no `BuildServiceProvider()` bootstrap, no string-keyed dictionaries. Each LLM call dispatches a separate `RunDurableAgentStep` activity; each tool call dispatches a separately named `InvokeAgentTool` activity (per-agent local registry, distinct from MEAI's flat `InvokeFunction`). The library composes the chat pipeline with `UseProvidedChatClientAsIs = true` so users do NOT call `.UseFunctionInvocation()` on their `IChatClient`.

`ConfigureAgentPipeline` is dry-built once at startup in a validation scope and built once per
`RunDurableAgentStep` activity attempt in that attempt's DI scope. No pipeline is cached in the
blueprint. Custom wrappers must be transparent, non-disposable `DelegatingAIAgent` instances;
MAF's built-in `OpenTelemetryAgent` is owned and disposed by the per-build pipeline lease,
including when local validation rejects a successfully built chain. A partial chain hidden by an
exception from MAF's `AIAgentBuilder.Build()` is not available for package-side cleanup.
During a live run, outer middleware and `TemporalAgentContext.Current.CurrentSession` share the
exact restored `TemporalAgentSession`; middleware may make retry-safe StateBag changes but cannot
replace the session. An innermost boundary passes `null` only to `ChatClientAgent`, which creates
its required transient `ChatClientAgentSession`.

**Configuration**: see `docs/how-to/MAF/usage.md` for the full `TemporalAgentsOptions` reference. Worker-level defaults are prefixed `Default*` (e.g. `DefaultActivityTimeout`, `DefaultHeartbeatTimeout`, `DefaultApprovalTimeout`, `DefaultMaxEntryCount`, `DefaultRetryPolicy`, `DefaultHistoryReducer`, `DefaultTimeToLive`); per-agent overrides on `DurableAgentBuilder` use the unprefixed names. Inheritance rule: `effective = registration.X ?? options.DefaultX`.

**Two agent types** (use the right one for context):
- `TemporalAIAgent` — workflow-context sub-agent. Access via `TemporalWorkflowExtensions.GetTemporalAgent("Name")`.
- `TemporalAIAgentProxy` — external-context proxy. Access via `services.GetTemporalAgentProxy("Name")`.

**HITL**: see `docs/how-to/MAF/hitl-patterns.md`. Activity timeout must accommodate human review time.

**StateBag persistence**: **64 KB size guard** — `CreateContinueAsNewException` emits `LogWarning` when the serialized `CarriedStateBag` exceeds 64 KB. Prune or externalize StateBag contents when this fires.

**Per-tool Temporal activities** are the default behavior: every `AddDurableAgent` runs the durable loop (each LLM call is a `RunDurableAgentStep` activity; each tool call is a separately named `InvokeAgentTool` activity). Configure per-tool retry/timeout via the `DurableToolOptions` callback on `agent.AddTool(tool, opts => opts.NoRetry())` — write tools must call `.NoRetry()` (or set `MaximumAttempts = 1`) to prevent double-execution on retry. Cap loop iterations via `agent.MaxToolCallsPerTurn` (default 20). See `docs/how-to/MAF/durable-agents.md`.

**OpenTelemetry**: Register all four `ActivitySource` names with the tracer provider. **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic during replay; use `ActivitySourceExtensions.TrackWorkflowDiagnosticActivity` instead. See `docs/how-to/MAF/observability.md`.

For full API surface, see `docs/how-to/MAF/usage.md`.

---

## Critical: Durability and Determinism

**MUST READ**: [`docs/architecture/MAF/durability-and-determinism.md`](./docs/architecture/MAF/durability-and-determinism.md)

When a worker crashes:
- ✅ Completed agent calls are **not re-executed** — results replay from history
- ✅ `_currentStateBag` carries forward through `AgentWorkflowInput.CarriedStateBag`
- ✅ Conversation history is serialized in workflow state across continue-as-new transitions

---

## Important Dependencies and Notes

### Microsoft Agent Framework
- `TemporalCommunity.Extensions.Agents` depends on `TemporalCommunity.Extensions.AI` (which transitively brings in MEAI).
- HITL types are MEAI-side: `DurableApprovalRequest` / `DurableApprovalDecision` (from `TemporalCommunity.Extensions.AI.Approvals`).
- `AgentResponse`, `AIAgent`, `DelegatingAIAgent`, `AgentRunOptions` → `Microsoft.Agents.AI`.
- `ChatClientAgentRunOptions` → `Microsoft.Agents.AI` (not the Hosting package).
- `AgentSessionStateBag.Count` available; `AgentSessionStateBag.Serialize()` uses its own `AgentAbstractionsJsonUtilities.DefaultOptions`.

### Key Type Locations (gotchas)
- `RpcException` — `Temporalio.Exceptions` (NOT `Grpc.Core`)
- `Workflow.CreateContinueAsNewException` — takes `Expression<Func<TWorkflow, Task>>` (no collection expressions inside)
- `WorkflowIdConflictPolicy.UseExisting` — `Temporalio.Api.Enums.V1`
- `CompactionMarkerEntry` — `TemporalCommunity.Extensions.AI.Session` (lives in the AI library so both source-gen contexts see the `"compaction-marker"` discriminator). Polymorphic subtype of `DurableSessionEntry`. `CompactedAt` is a `[JsonIgnore]` alias of `CreatedAt` (no wire duplication).
- `DurableCompactionMarkerException`, `DurableMixedPatternException`, `DurableChatClientFactoryNotFoundException` — `TemporalCommunity.Extensions.AI.Exceptions`. Marker exception is `[Experimental("TA002")]`; the others are stable.
- `DurableChatStepResult` — `TemporalCommunity.Extensions.AI` (internal sealed) — Pattern 3 activity return type from `GetChatStepAsync`; carries `IsFinal`, `AssistantMessage`, optional `ToolCalls` and `Usage`.
- `DurableChatToolOptions` — `TemporalCommunity.Extensions.AI` (public sealed) — per-tool options builder for Pattern 3; mirrors MAF's `DurableToolOptions` verbatim.
- `IDurableToolInterceptor<in TContext>` — `TemporalCommunity.Extensions.AI.Tools` — cross-library interceptor interface. `BeforeToolCallAsync(TContext, CancellationToken) → Task<DurableToolDecision>`. `in` variance: `IDurableToolInterceptor<DurableToolContext>` is assignable to `IDurableToolInterceptor<AgentToolContext>`.
- `DurableToolDecision` — `TemporalCommunity.Extensions.AI.Tools` — return type of `BeforeToolCallAsync`. Static factories: `Proceed(...)`, `PauseForApproval(description)`, `Skip(syntheticResult)`, `Block(reason)`. Not wire-serialized (internal DTO is the serialized form).
- `DurableToolContext` — `TemporalCommunity.Extensions.AI.Tools` — cross-library base context. Properties: `ToolName`, `Arguments`, `CallId`, `SessionId?`. Non-sealed — `AgentToolContext` extends it.
- `IAgentToolInterceptor` — `TemporalCommunity.Extensions.Agents.Tools` — convenience alias for `IDurableToolInterceptor<AgentToolContext>`. Register via `agent.AddToolInterceptor(sp => ...)` or `opts.DefaultToolInterceptor`. Returns `DurableToolDecision` from the AI library.
- `AgentToolContext` — `TemporalCommunity.Extensions.Agents.Tools` — extends `DurableToolContext`. Adds `AgentName` (required) and `StateBag?` (read-only snapshot). The inherited `SessionId` is populated from `ActivityExecutionContext.Current.Info.WorkflowId` in the interceptor activity.
- `WorkingSetContextProvider` — `TemporalCommunity.Extensions.Agents` — `AIContextProvider` subclass that extracts recently-referenced file paths from accumulated `ChatMessage` history and injects a compact working-set note before each LLM call. Stores result in `AgentSessionStateBag["temporal.working_set"]`.

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
- **Search-attribute pre-registration is required by default.** `EnableSearchAttributes` now defaults to `true`. Agents integration tests must use `TestEnvironmentHelper.StartLocalAsync()` (which pre-registers `AgentName`/`SessionCreatedAt`/`TurnCount`) unless `opts.EnableSearchAttributes = false` is explicitly set. Bare `WorkflowEnvironment.StartLocalAsync()` is only sufficient when search attributes are disabled. AI integration tests never need pre-registration.
- **Both suites use a pinned embedded server** —
  `TemporalServiceTestEnvironment.StartLocalAsync()` uses Temporal CLI `v1.8.0` (Server 1.31.2)
  and verifies `GetSystemInfo`. Agents tests call it through `TestEnvironmentHelper` for search
  attributes. Do not add bare `WorkflowEnvironment.StartLocalAsync()` calls.

---

## Workflow Best Practices

### ✅ DO
- Use the fluent `.AddTemporalAgents()` builder
- Use `GetTemporalAgent()` inside workflows for sub-agent orchestration
- Use `Workflow.UtcNow` and `Workflow.NewGuid()` (not `DateTime.UtcNow` / `Guid.NewGuid()`)
- Set appropriate per-agent TTLs (default: 14 days)
- Validate config eagerly — `string.IsNullOrEmpty` + `InvalidOperationException` for missing config (not `is null` + `ArgumentNullException`)
- Keep OTel spans out of workflows — `agent.turn` lives in `AgentActivities`; `agent.client.send` in `DefaultTemporalAgentClient`
- For non-idempotent write-style tools (send email, write record), pass `opts => opts.NoRetry()` to `agent.AddTool(...)` so the tool's activity does not re-execute on transient failure

### ❌ DON'T
- **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic on replay
- Don't use wall-clock time in workflows (`DateTime.UtcNow`, `DateTimeOffset.Now`)
- Don't use `Random` or `Guid.NewGuid()` in workflows
- Don't construct `DurableChatClient`/`DurableEmbeddingGenerator`/`ChatClientBuilder` composition inside `[WorkflowRun]`/`[WorkflowUpdate]` methods — see `docs/architecture/MEAI/direct-adapter-anti-pattern.md`

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
just test-individual tests/TemporalCommunity.Extensions.AI.IntegrationTests       # per-test loop, 180s default cap, reports PASS/FAIL/HANG
just test-individual tests/TemporalCommunity.Extensions.AI.IntegrationTests Pattern3 300  # filter + custom cap

# When a single test command hangs (pipe-buffering hides output):
just test-logged tests/TemporalCommunity.Extensions.Agents.IntegrationTests       # writes to /tmp log, 600s default cap

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
just test-samples-maf      # 11 MAF samples, same
just test-samples          # both
just verify-sample-coverage # drift detector — fails if a new sample dir isn't in the recipe lists
just clean-test-artifacts  # remove artifacts/{test-individual,sample-runs}/
```

**Skipped from sample-canary** (must run manually):
- `samples/{MEAI,MAF}/HumanInTheLoop` — interactive (Console.ReadLine)
- `samples/MAF/ApprovalScopes` — interactive (Console.ReadLine)
- `samples/MAF/SplitWorkerClient` — two processes (Worker + Client)

**Pre-requisites for sample-canary:** GNU coreutils `timeout` (macOS: `brew install coreutils`),
`nc` (netcat), `OPENAI_API_KEY` in env or user-secrets, and Temporal Service 1.31.0 or newer on
`localhost:7233` (local: `temporal server start-dev`). `_sample-preflight` queries the connected
service version with the Temporal CLI and fails closed; it does not infer the service version from
the CLI binary's own version.

### Versioning

**Versions** auto-derive from git tags via MinVer: exactly on `X.Y.Z` tag → `X.Y.Z`; N commits after → `X.Y.(Z+1)-preview.N`. Cut a release with `git tag -a X.Y.Z -m "..."` then `just pack`. **Tags must NOT have a `v` prefix** — `Directory.Build.props` does not set `<MinVerTagPrefix>`, so MinVer's default (no prefix) applies. Existing tags follow this convention (`0.1.0`, `0.1.1`, ..., `0.3.0`).

**Publish**: to NuGet.org, either `just publish-nuget` (local — needs `NUGET_API_KEY` env var) or the `.github/workflows/publish.yml` workflow (`workflow_dispatch`, OIDC Trusted Publishing — no stored API key/secret; the `nuget-publish` GitHub environment must be configured). Remember: tags carry no `v` prefix.

---

## CI/CD — GitHub Actions

`.github/workflows/build.yml`. Two jobs: `build` (ubuntu+macOS matrix on push to `main`, runs `just build` + `just test-unit`) and `package` (after `build`, `just pack`, uploads artifact).

`.github/workflows/integration.yml`. Runs the discovered integration suites on pull requests to `main`, pushes to `main`, and manual dispatch. Each matrix entry restores, builds, and runs its selected suite with `Category!=HistoryCapture`; history-capture tests remain excluded from that workflow.

`.github/workflows/publish.yml`. `workflow_dispatch`-only `publish` job (`nuget-publish` environment, `id-token: write`). Verifies MinVer resolved a real tag (fails on the `0.0.0-*` fallback), `just pack`, then publishes to NuGet.org via OIDC Trusted Publishing (`NuGet/login@v1` exchanges the GitHub OIDC token for a short-lived key — no long-lived secret stored).

**Required secrets**: none — publishing uses OIDC Trusted Publishing.

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
dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj         # per-tool Temporal activities with write-tool no-retry
dotnet run --project samples/MAF/ContextProviders/ContextProviders.csproj           # TodoProvider + AgentModeProvider via AddContextProvider
dotnet run --project samples/MAF/Skills/Skills.csproj                               # UseSkills: file-based + inline skills with progressive-disclosure load_skill

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
| `InvalidOperationException` from `TemporalAIAgent` (called outside workflow) | `TemporalAIAgent` is workflow-context only. Obtain it via `TemporalWorkflowExtensions.GetTemporalAgent` inside a `[Workflow]` method. For external callers, use `services.GetTemporalAgentProxy("Name")` instead. |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Don't serialize via `DefaultOptions`; use `StateBag.Serialize()` |
| Activity timeout (HITL) | Increase `DefaultActivityTimeout` (or per-agent `ActivityTimeout`) to accommodate human review time |
| Worker won't start | `temporal server start-dev` running on `localhost:7233`? |
| Search attributes missing in UI, or workflow start fails with "no mapping defined for search attribute" | `opts.EnableSearchAttributes` defaults to `true`; `AgentName`/`SessionCreatedAt`/`TurnCount` must be pre-registered before the worker starts — this is **not** automatic, even for a local `temporal server start-dev`. Start it with `--search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`; production clusters need the equivalent one-time CLI commands. |
| Integration test "Unexpected workflow task failure" | `EnableSearchAttributes` defaults to `true` — use `TestEnvironmentHelper.StartLocalAsync()`, or set `opts.EnableSearchAttributes = false` to disable search attribute upserts |
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

### TemporalCommunity.Extensions.Agents (MAF)

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
- **Tool Interceptor**: `docs/how-to/MAF/tool-interceptor.md`
- **Do's and Don'ts**: `docs/how-to/MAF/dos-and-donts.md`
- **Durability Guarantees**: `docs/architecture/MAF/durability-and-determinism.md`
- **Sessions and Workflow Loop**: `docs/architecture/MAF/agent-sessions-and-workflow-loop.md`
- **Pub/Sub Equivalents**: `docs/architecture/MAF/pub-sub-and-event-driven.md`
- **StateBag and AIContextProvider**: `docs/architecture/MAF/session-statebag-and-context-providers.md`
- **Agent-to-Agent Communication**: `docs/architecture/MAF/agent-to-agent-communication.md`

### TemporalCommunity.Extensions.AI (MEAI)

- **Usage Guide**: `docs/how-to/MEAI/usage.md`
- **Tool Functions**: `docs/how-to/MEAI/tool-functions.md` (direct durable calls, managed sessions, worker-owned toolsets, and invocation-scoped factories)
- **Embeddings**: `docs/how-to/MEAI/embeddings.md`
- **Testing**: `docs/how-to/MEAI/testing.md`
- **Observability**: `docs/how-to/MEAI/observability.md`
- **HITL Patterns**: `docs/how-to/MEAI/hitl-patterns.md`
- **Custom Workflow Output**: `docs/how-to/MEAI/custom-workflow-output.md`
- **Durable Chat Pipeline**: `docs/architecture/MEAI/durable-chat-pipeline.md`
- **Direct-Adapter Anti-Pattern**: `docs/architecture/MEAI/direct-adapter-anti-pattern.md`
- **Cross-Library Integration**: `docs/architecture/MEAI/cross-library-integration.md`
