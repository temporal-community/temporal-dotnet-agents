# Changelog

## [Unreleased]

- Fixed continue-as-new input cloning so `HistoryReducerKey`, retry/tool/interceptor settings, and
  derived workflow input fields are preserved without field-by-field reconstruction.
- Added `IDurableChatWorkflowInputFactory` so application-owned workflows can create the same
  replay-frozen start configuration as the built-in managed session client, including when the
  default workflow is disabled.
- Added the provisional `DurableToolWorkflowBase<TRequestData, TTurnState>` typed-turn contract,
  numeric dispatch/completion wire values, and package-owned model/tool loop composition while
  retaining `DurableChatWorkflowBase<TOutput>` as the low-level custom orchestration base.
- Added frozen model-facing function declarations and invocation-scoped implementation factories
  with typed request/state context, structural schema validation, ordinary MEAI function/decorator
  support, and explicit combined or split-process registration.
- Added sequential typed-turn dispatch as the specialized-base default, threading successful
  explicit state replacements in model-call order while preserving the stock session's parallel
  behavior and the all-approvals-before-tools safety barrier.
- Added versioned, retry-stable activity idempotency metadata using a fixed domain-separated,
  length-prefixed strict-UTF-8 SHA-256 v1 algorithm, with unknown versions rejected before tool
  construction or invocation.
- Added a domain-neutral `ExtensibleDurableTurns` sample covering ordinary functions,
  invocation-scoped MEAI decoration, authoritative authorization, sequential typed state,
  per-tool activities, and an idempotent retry after an injected post-write failure.
- Added worker-free declaration and workflow-input registration for split client/worker hosts.
- Failed durable turns now roll back their request, partial response, and turn count so a later
  turn cannot inherit an unsuccessful request as conversation history.
- Failed durable agent turns now also roll back application, provider, interceptor, and tool
  StateBag mutations inside the serialized turn boundary while retaining approval-scope records
  committed by approval updates; queued failed turns cannot erase a preceding turn's committed
  StateBag changes.
- Added parked-approval, fire-and-forget rollback, and checked-in AgentWorkflow replay regressions,
  plus a locally runnable StateBag rollback timing/allocation benchmark.
- Library-owned durable-tool configuration and state-completion failures now fail the turn
  immediately instead of being returned to the model as recoverable tool output.
- Sequential typed turns now stop before scheduling later tools in the same model batch after a
  fatal configuration or state-completion failure.
- Failed model activities use the consecutive-error allowance without consuming the successful
  model/tool iteration budget; permanent provider errors fail immediately.
- Documented that managed tool sessions started on 0.10.4 must be drained before upgrading because
  they do not contain the frozen tool declarations required by this release.

## [0.10.4] - 2026-08-09

- Updated the tested dependency baseline to Microsoft.Extensions.AI 10.8.3 and
  Microsoft.Agents.AI 1.17.0, including their required OpenAI, logging, and
  System.Text.Json dependency floors.
- Updated Temporalio, Temporalio.Extensions.Hosting, and
  Temporalio.Extensions.OpenTelemetry to 1.17.0.
- Fixed direct `UseDurableExecution()` chat calls so both non-streaming and buffered-streaming
  workflow continuations remain on Temporal's workflow task scheduler.
- Preserved serializable chat routing/decorator metadata through durable transport, normalized its
  converter representation, retained ordinary MEAI options, and removed Temporal-private keys at
  the provider boundary for direct and managed chat calls.
- Added startup and activity-time topology validation that rejects MAF middleware factories which
  replace or hide the library-created `ChatClientAgent` instead of preserving it through a
  `DelegatingAIAgent` chain.
- Built MAF middleware from validation/activity DI scopes, removed the extra blueprint-time build,
  and closed the known `OpenTelemetryAgent` lifetime while rejecting ownership-ambiguous custom
  disposable wrappers.
- Passed the restored `TemporalAgentSession` through outer MAF middleware, persisted its StateBag
  mutations, rejected session replacement, and translated only at the `ChatClientAgent` leaf.
- Retained the Temporal `agent.turn` span with upstream telemetry, enriched the actual sampled MAF
  `invoke_agent` ancestor with its correlation ID, and assigned GenAI usage to exactly one span.
- Routed direct chat and embedding activities to `DurableExecutionOptions.TaskQueue`, enabling
  workflow and AI activity workers to poll separate queues as the option contract specifies.
- Disposed reachable MAF `OpenTelemetryAgent` wrappers when local pipeline validation rejects a
  successfully built chain, and added failure, cancellation, retry, and identity regressions for
  per-attempt middleware ownership.
- Added regression coverage proving the durable MAF session context is restored to surrounding
  middleware after provider exceptions and activity cancellation.
- Documented and tested that `AIFunction.AsDurable()` activities use the calling workflow's task
  queue and are not rerouted by `DurableExecutionOptions.TaskQueue`.
- Raised the minimum supported Temporal Service version to 1.31.0, pinned embedded tests to
  Temporal CLI `v1.8.0` (Server 1.31.2), and added fail-closed service-version checks for tests,
  smoke coverage, and sample preflight.

## [0.10.0] - 2026-07-13

`0.10.0` establishes the durable-execution baseline for this repository's two
pre-release libraries.

### `TemporalCommunity.Extensions.AI`

- Durable `IChatClient` sessions backed by Temporal workflows, durable updates,
  persisted conversation history, and continue-as-new.
- Explicit managed-session tools registered with `AddDurableTools`. Each model
  step and each requested tool invocation executes as a separate Temporal
  activity with independently configurable retry and timeout behavior.
- Pre-tool interception and workflow-owned human approval, including pending
  approval queries, expiry, resolution, and shutdown.
- Direct custom-workflow adapters for chat clients, embedding generators, and
  explicitly invoked `AIFunction` values.
- Keyed chat-client resolution for managed sessions.

### `TemporalCommunity.Extensions.Agents`

- Durable Microsoft Agent Framework sessions built from a registered
  `IChatClient` as a `ChatClientAgent`; model steps and tools are Temporal
  activities owned by the agent workflow.
- Durable per-tool retry, timeout, interception, and approval behavior.
- Per-step `AIContextProvider` execution, `AgentSessionStateBag` write-back,
  and explicitly declared static provider tools.
- External and workflow-local agent proxies, typed output, routing,
  orchestration, scheduling, observability, and continue-as-new support.
- Completed request/response execution only; `RunStreamingAsync` is not
  supported for durable agents.

### Supported contract

- The MEAI managed-session path accepts a bare `IChatClient` and registered
  durable tools. It rejects caller-supplied `ChatOptions.Tools` and inline
  function-invocation middleware.
- The MAF path accepts the library-built `ChatClientAgent` shape. Arbitrary
  `AIAgent` instances, `A2AAgent`, graph agents, `HarnessAgent`, and providers
  that own history, dynamic tools, or live process state are outside the
  durable contract.
- Both packages ship `net10.0` and `netstandard2.1` assets. .NET Framework is
  not supported.
