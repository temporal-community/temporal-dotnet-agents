# Durable Extensions vNext — Implementation Tracker

**Status:** Implementation complete locally — remote CI verification pending the `0.10.2` push.

**Scope:** Breaking pre-release redesign of `TemporalCommunity.Extensions.AI` first, followed by `TemporalCommunity.Extensions.Agents`.

**Tracking rule:** A checkbox is checked only after its implementation is merged and its listed verification evidence is recorded in the implementation PR or commit. A phase is not complete until its documentation, samples, and verification checkboxes are also complete.

## Remote tracking and CI evidence

GitHub Issues are enabled for `temporal-community/temporal-dotnet-agents`. Use one issue per implementation phase as the primary progress record; this committed checklist supplies the detailed acceptance criteria for those issues.

- [x] Create and link the phase issues before implementation begins:
  - [#1 — Contract and characterization baseline](https://github.com/temporal-community/temporal-dotnet-agents/issues/1)
  - [#2 — MEAI package boundary](https://github.com/temporal-community/temporal-dotnet-agents/issues/2)
  - [#3 — MEAI managed tool loop](https://github.com/temporal-community/temporal-dotnet-agents/issues/3)
  - [#4 — MEAI documentation, samples, and exit gate](https://github.com/temporal-community/temporal-dotnet-agents/issues/4)
  - [#5 — MAF compatibility spike](https://github.com/temporal-community/temporal-dotnet-agents/issues/5)
  - [#6 — MAF bounded agent and provider support](https://github.com/temporal-community/temporal-dotnet-agents/issues/6)
  - [#7 — MAF removals and targeted fixes](https://github.com/temporal-community/temporal-dotnet-agents/issues/7)
  - [#8 — MAF documentation, samples, and combined exit gate](https://github.com/temporal-community/temporal-dotnet-agents/issues/8)
- [ ] Each implementation PR/commit links its issue, completed checklist items, and verification evidence.
- [ ] The GitHub Actions **Build** workflow is green: Ubuntu and macOS build/unit-test jobs, plus the package job.
- [ ] The GitHub Actions **Integration Tests** workflow is green: both `Integration (AI)` and `Integration (Agents)` jobs.
- [ ] CodeQL is green when triggered for the branch/commit.
- [x] Local documentation-link checks and sample build/run evidence are recorded; these are not substitutes for CI and are not currently covered by the observed CI jobs.

### Local verification evidence

- `just test` — Release build plus all unit and integration tests passed.
- `just smoke-downlevel-proxy` — packed `netstandard2.1` consumer gate passed for both libraries.
- `just test-samples-meai` — 7 pass / 0 fail / 0 hang, including the workflow-owned
  approval sample.
- `just test-samples-maf` — 14 pass / 0 fail / 0 hang; the interactive MAF approval
  sample was also run and approved manually.
- `just verify-sample-coverage` and the local Markdown link check passed.
- Temporal Web event-history review confirmed one model-step activity per LLM call, one
  activity per tool call, and a visible retry attempt boundary.

## Contract and operational boundary

- [x] Publish the vNext durable-execution contract: Temporal owns the model/tool loop, tool retries, approvals, and durable history.
- [x] Record the pre-release baseline: no in-flight executions or legacy preview compatibility path are supported.
- [x] Document the supported inputs: MEAI `IChatClient` and library-built MAF `ChatClientAgent` only.
- [x] Document exclusions: `FunctionInvokingChatClient`, arbitrary durable `ChatOptions.Tools`, generic `AIAgent`, A2A, `HarnessAgent`, streaming, and external history.
- [x] Document StateBag and approval limits for `TemporalAIAgent` and `AgentJobWorkflow`.
- [x] Record the deferred Temporal-native harness profile direction; do not promise `HarnessAgent` compatibility.

## MEAI — package boundary and managed tools

- [x] Add characterization tests for bare chat, durable tools, approvals, retry, keyed clients, and continue-as-new.
- [x] Add a packaged bare-MEAI consumer test that proves no MAF dependency is required.
- [x] Remove the `Microsoft.Agents.AI` package reference and Agent-specific internals visibility from `TemporalCommunity.Extensions.AI`.
- [x] Replace the shared `AgentChainWalker` with an MEAI-only `IChatClient` walker.
- [x] Move the Agents `AIAgent` traversal/validation implementation into the Agents project.
- [x] Make explicit durable-tool registration the only source of tools for a durable MEAI session.
- [x] Reject non-empty caller-supplied `ChatOptions.Tools` for durable requests with a contract-focused error.
- [x] Implement the workflow-owned LLM-step → durable-tool-activity → LLM-step loop.
- [x] Reject `FunctionInvokingChatClient` at startup when observable and at activity time as a keyed/factory backstop.
- [x] Add model/tool-loop tests: multi-tool, ordering, retry, tool errors, loop limit, approval, keyed-client rejection, replay, and continue-as-new.

### MEAI documentation and samples

- [x] Rewrite `docs/how-to/MEAI/tool-functions.md` around the single supported managed-tool model.
- [x] Rewrite `docs/architecture/MEAI/durable-chat-pipeline.md`; remove durable examples using `UseFunctionInvocation()`.
- [x] Update the MEAI and root READMEs, testing, observability, HITL, and custom-workflow documentation.
- [x] Update `samples/MEAI/DurableChat` to show explicit durable-tool registration.
- [x] Update `samples/MEAI/DurableTools` to show the workflow-owned tool loop and activity behavior.
- [x] Update MEAI HITL and tool-interceptor samples to use the new tool contract.
- [x] Build every MEAI sample project and run the canonical tool/HITL samples against local Temporal.

### MEAI exit gate

- [x] MEAI unit, integration, replay, and compatibility tests pass.
- [x] MEAI package builds for all target frameworks.
- [x] Bare-MEAI packaged-consumer test passes with no MAF package dependency.
- [x] Documentation links and code snippets are verified.
- [x] MEAI samples build and required canonical samples run successfully.

## MAF — compatibility spike and bounded durable agent

- Research record: [MAF bounded durable-agent compatibility](../docs/architecture/MAF/bounded-durable-agent-compatibility.md). The checklist remains unchecked until the work is merged and its verification evidence is recorded.
- [x] Run and record a MAF 1.12 compatibility spike for `ChatClientAgent`, decorators, `AIContextProvider`, StateBag, and `IDurableToolSource`.
- [x] Publish the resulting provider/decorator support matrix: supported, static-tool supported, or rejected.
- [x] Build the MAF `ChatClientAgent` internally from the registered `IChatClient`; do not accept arbitrary `AIAgent` instances.
- [x] Reuse MEAI durable tool-loop/wire infrastructure where it is genuinely common.
- [x] Preserve per-LLM-step `AIContextProvider` invocation and StateBag write-back.
- [x] Retain static provider tool registration through `IDurableToolSource` and `AddContextProvider(provider, durableTools)`.
- [x] Reject or disable dynamic-tool, history-owning, live-process-state, and inline-function-invocation providers.
- [x] Keep provider/chat-client/interceptor construction inside the per-activity DI scope; correct stale caching documentation.
- [x] Add provider tests for scoped dependencies, StateBag persistence, static tools, unsupported providers, and no cross-session instance sharing.

## MAF — removal and targeted fixes

- [x] Remove `IAgentHistoryStore`, its registration/activities/tests, its documentation, and `samples/MAF/ExternalHistoryStore`.
- [x] Make streaming uniformly throw `NotSupportedException`; remove synthetic completed-response streaming.
- [x] Reject a `TemporalAIAgentProxy` call when the supplied session belongs to another agent.
- [x] Make only `RunAgentFireAndForgetAsync` start-and-signal atomically; leave already-atomic send/delayed paths alone.
- [x] Document and test: `TemporalAIAgent` carries StateBag for LLM-step providers but not StateBag-backed approval scopes; `AgentJobWorkflow` has neither StateBag nor workflow-parked approval.

### MAF documentation and samples

- [x] Update MAF usage, session/StateBag, agent-loop, provider, and Harness compatibility documentation to the bounded contract.
- [x] Remove external-history documentation rather than leaving an unsupported tutorial.
- [x] Update `samples/MAF/BasicAgent` as the canonical durable-agent baseline.
- [x] Update `samples/MAF/PerToolActivities` as the canonical per-tool activity/retry example.
- [x] Update `samples/MAF/ContextProviders`, `DurableContextProvider`, `WorkingSet`, and `Skills` for per-step provider and static-tool rules.
- [x] Update MAF HITL and tool-interceptor samples for Temporal-owned approval/interception.
- [x] Build every MAF sample project and run the canonical agent, tool, provider, and HITL samples against local Temporal.

### MAF and combined exit gate

- [x] MAF unit, integration, replay, and compatibility tests pass.
- [x] MAF package builds for all target frameworks.
- [x] Packaged Agents consumer and down-level smoke tests pass.
- [x] Documentation links and code snippets are verified.
- [x] MAF samples build and required canonical samples run successfully.
- [x] Full solution build and test pass.
- [x] Manual Temporal Web review confirms one LLM activity per model step, one activity per tool, visible retry boundaries, and no inline tool execution.

## Progress reporting format

For every implementation update, report:

1. Checkboxes completed and remaining in the active phase.
2. Files/API changed and any breaking migration note.
3. Tests and sample commands run, with results.
4. Documentation/sample changes completed or explicitly remaining.
5. Whether the phase exit gate is met.
