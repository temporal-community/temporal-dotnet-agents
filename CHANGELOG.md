# Changelog

## [Unreleased]

- Updated the tested dependency baseline to Microsoft.Extensions.AI 10.8.3 and
  Microsoft.Agents.AI 1.17.0, including their required OpenAI, logging, and
  System.Text.Json dependency floors.
- Fixed direct `UseDurableExecution()` chat calls so both non-streaming and buffered-streaming
  workflow continuations remain on Temporal's workflow task scheduler.
- Preserved serializable chat routing/decorator metadata through durable transport, normalized its
  converter representation, retained ordinary MEAI options, and removed Temporal-private keys at
  the provider boundary for direct and managed chat calls.
- Added startup and activity-time topology validation that rejects MAF middleware factories which
  replace or hide the library-created `ChatClientAgent` instead of preserving it through a
  `DelegatingAIAgent` chain.

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
