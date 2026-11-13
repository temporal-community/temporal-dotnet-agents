# TemporalAgents

Two Temporal .NET SDK integrations for durable AI applications:

- `TemporalCommunity.Extensions.Agents` — durable agent sessions on Microsoft Agent Framework. Depends on the AI library. Library guide: [`docs/how-to/MAF/usage.md`](./docs/how-to/MAF/usage.md).
- `TemporalCommunity.Extensions.AI` — makes a plain MEAI `IChatClient` durable, no Agent Framework. Library guide: [`docs/how-to/MEAI/usage.md`](./docs/how-to/MEAI/usage.md).

Docs: how-to guides in `docs/how-to/{MAF,MEAI}/`, design docs in `docs/architecture/`, and
combining the libraries in [`docs/library-combinations.md`](./docs/library-combinations.md).
**Read before touching workflow code:** [`docs/architecture/MAF/durability-and-determinism.md`](./docs/architecture/MAF/durability-and-determinism.md).

## Basics

- .NET 10; both libraries also ship `netstandard2.1`. Solution is `TemporalAgents.slnx` (not `.sln`).
- Minimum Temporal Service is 1.31.0. SDK pin is `global.json`; versions come from MinVer.
- All build/test/pack entry points are `just` recipes: `just --list`.

## Policy and release

- Both packages are published on NuGet.org. Pre-v1, breaking changes are fine and no migration
  guides or changelogs are wanted. But a behaviour change under an existing key or signature is a
  real break for consumers: state the new contract in the docs instead of narrating the old one.
- MinVer tags have **no `v` prefix** (`0.14.2`, not `v0.14.2`). Untagged commits version as a
  prerelease of the next patch.
- Publish with `just publish-nuget` (needs `NUGET_API_KEY`), or `.github/workflows/publish.yml`
  (`workflow_dispatch`, `nuget-publish` environment, OIDC Trusted Publishing, no stored secret).
  The workflow takes a `release_type` input. `preview` requires MinVer to compute a prerelease
  version. `official` requires the dispatched commit to carry an exact tag that matches MinVer and
  is stable SemVer (`X.Y.Z`). Either way it packs, runs the packed-package and down-level-runtime
  gates, then pushes.

## Registration (both libraries)

- For co-hosted applications, register `services.AddHostedTemporalWorker(...)` then
  `.AddDurableAI(...)` or `.AddTemporalAgents(...)`. Split MEAI deployments use
  `services.AddDurableAIClient(...)` and worker-builder `.AddDurableAIWorker(...)`.
  No plugin wrappers are shipped; add plugins through
  `TemporalWorkerOptions.Plugins` / `TemporalClientConnectOptions.Plugins`.
- `DurableAIDataConverter.Instance` must be on the Temporal client or `AIContent` subtypes lose
  `$type` and silently deserialize as the base type. Both libraries fail fast on
  a manual client with an incompatible converter. `AddTemporalClient(...)` swaps it in only while the
  converter is still the SDK default; `AddHostedTemporalWorker(addr, ns, queue)` and the plugin
  paths also wire it. Callers of `TemporalClient.ConnectAsync` must set it themselves.
- Do not add `.UseFunctionInvocation()` to managed MEAI session clients or Agents clients;
  the durable workflow owns the tool loop.

## Determinism

- Never call `ActivitySource.StartActivity()` inside `[Workflow]`; it is non-deterministic on
  replay. Spans live in activities and clients (see `docs/how-to/MAF/observability.md`).
- Use `Workflow.UtcNow` and `Workflow.NewGuid()`; never `DateTime.UtcNow`, `Random` or
  `Guid.NewGuid()` in workflows.
- Do not construct `DurableChatClient`/`ChatClientBuilder` composition inside
  `[WorkflowRun]` / `[WorkflowUpdate]` methods:
  [`direct-adapter-anti-pattern.md`](./docs/architecture/MEAI/direct-adapter-anti-pattern.md).
- OpenTelemetry sources to register: each library's `ActivitySourceName` constant
  (`DurableChatTelemetry`, `TemporalAgentTelemetry`) plus Temporalio's `TracingInterceptor`
  `ClientSource` / `WorkflowsSource` / `ActivitiesSource`. See `docs/how-to/MAF/observability.md`.

## Cross-library invariants

- **Interceptor contract lives in the AI library** (`TemporalCommunity.Extensions.AI.Tools`):
  `IDurableToolInterceptor<in TContext>`, `DurableToolDecision`, `DurableToolContext`.
  `IAgentToolInterceptor` and `AgentToolContext` (Agents) extend them. The `in` variance is
  load-bearing: a `DurableToolContext` interceptor must stay assignable to the agent one.
  `DurableToolDecision` is not wire-serialized.
- **Agent pipeline** (`ConfigureAgentPipeline`): wrappers must be transparent, non-disposable
  `DelegatingAIAgent` instances. The pipeline is built per `RunDurableAgentStep` attempt and
  no pipeline is cached. Middleware may make retry-safe StateBag changes but cannot replace the
  session. Details: `docs/how-to/MAF/llm-call-interception.md`.
- `AIFunction.AsDurable()` activities run on the calling workflow's task queue.
  `DurableExecutionOptions.TaskQueue` does not apply to them.
- MEAI managed sessions reject caller-supplied `ChatOptions.Tools` and inline
  `UseFunctionInvocation()` loops: `docs/how-to/MEAI/managed-session-tool-rules.md`.
- Write-style (non-idempotent) tools must not retry: `agent.AddTool(tool, opts => opts.NoRetry())`
  (Agents) or `AddDurableTool(tool, opts => opts.NoRetry())` (AI). Otherwise a transient failure
  re-executes the side effect.
- `TemporalAgentSession` is **not** in any source-gen context; resolving it through
  `DefaultOptions.GetTypeInfo` silently uses reflection or fails with
  `GetTypeInfo metadata not provided`. Serialize through
  `TemporalAgentSessionSnapshot` (registered in `AgentSessionJsonContext`). Its `stateBag` and
  `history` are omitted when null, and a snapshot without `history` is the supported legacy shape
  that restores as empty history. `TemporalAgentSession.SerializeStateBag()` delegates to
  `StateBag.Serialize()`, not session serialization.
- **Options naming asymmetry is intentional; do not rename.** `DurableExecutionOptions` uses
  unprefixed names (`ActivityTimeout`). `TemporalAgentsOptions` worker-level defaults use
  `Default*` (`DefaultActivityTimeout`). Per-agent `DurableAgentBuilder` uses unprefixed names.
  Rule: `effective = registration.X ?? options.DefaultX`.
- Session ownership and StateBag merge rules: see
  [`session-statebag-and-context-providers.md`](./docs/architecture/MAF/session-statebag-and-context-providers.md).

## SDK and type gotchas

- `RpcException` is in `Temporalio.Exceptions`, not `Grpc.Core`.
- `WorkflowIdConflictPolicy` is in `Temporalio.Api.Enums.V1`.
- `Workflow.CreateContinueAsNewException` takes `Expression<Func<TWorkflow, Task>>`; no collection
  expressions inside the lambda.
- `CompactionMarkerEntry` lives in the AI library (`TemporalCommunity.Extensions.AI.Session`) so
  both source-gen contexts see its discriminator.
- HITL types `DurableApprovalRequest` / `DurableApprovalDecision` are in
  `TemporalCommunity.Extensions.AI.Approvals`, for both libraries.

## Build, test, triage

Core recipes: `just build`, `just test-unit` (no server), `just test-integration` (embedded
servers), `just test`, `just pack`, `just _verify-doc-links`. `just ci` runs clean, build, unit
tests, the sample-catalog, doc-link and doc-snippet gates, then pack. It does **not** run
integration tests or sample canaries (`just test`, `just test-samples`).

- **Search attributes.** Agents `EnableSearchAttributes` defaults to `true`; the AI library
  defaults to `false`. Pre-register before the worker starts, including on
  `temporal server start-dev` (`--search-attribute AgentName=Keyword ...`): Agents needs
  `AgentName` (Keyword), `SessionCreatedAt` (Datetime) and `TurnCount` (Int); the AI library, when
  enabled, needs only `SessionCreatedAt` (Datetime) and `TurnCount` (Long). Otherwise workflows fail with "no mapping defined
  for search attribute" or "Unexpected workflow task failure". Production needs the equivalent
  one-time CLI commands.
- Integration tests start servers via `TemporalServiceTestEnvironment.StartLocalAsync()` (Agents:
  through `TestEnvironmentHelper`, which registers the attributes), never bare
  `WorkflowEnvironment.StartLocalAsync()`.
- **Hung suite:** `just _test-individual <project> [filter] [limit]` reports PASS/FAIL/HANG per
  test; `just _test-logged <project>` writes to a `/tmp` log. Both are `_`-prefixed, so hidden
  from `just --list`. Never pipe `dotnet test` through `tail`; pipe buffering hides output.
- **Orphaned servers:** `just list-orphans`, then `just kill-orphans` (the SDK's extracted
  `temporal-sdk-dotnet` binary only) or `just kill-test-hosts` (path-scoped to this repo). Never
  bare `pkill`; it kills other projects' test hosts.
- **Worktrees:** `just cleanup-stale-worktrees` checks dirty state first and uses a single `-f`;
  never `git worktree remove -f -f` directly.
- **Sample canary:** exit code is not the assertion. Each sample must print its output markers.
  Interactive and multi-process samples are skipped on purpose; `just _verify-sample-coverage`
  catches drift. Needs `OPENAI_API_KEY`, GNU `timeout`, `nc`, and a server on `localhost:7233`;
  `_sample-preflight` checks them (`just doctor` covers only the temporal CLI, the OpenAI env vars
  and server reachability).
- **Doc gates** may use only POSIX tools and `python3`. `|| true` also swallows "command not
  found", so a missing dependency must fail loudly. No apostrophes in comments inside `<( ... )`;
  bash tracks quotes there and the parse breaks.
- **CI:** `.github/workflows/integration.yml` lists the two integration projects literally. Adding
  a third needs two new matrix lines; nothing detects the omission.
- Test authoring: `Assert.Throws<T>` requires the exact type, not a subtype. Prefer hand-written
  stubs (`StubAIAgent`, `TestChatClient`) over Moq/FakeItEasy. Patterns:
  `docs/how-to/MAF/testing-agents.md`, `docs/how-to/MEAI/testing.md`.
