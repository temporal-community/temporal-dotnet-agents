# Design Decisions

This document records the current boundaries of the two prerelease libraries. It intentionally
describes shipped behavior, not retired versions or speculative compatibility layers.

## Keep two libraries

`TemporalCommunity.Extensions.AI` makes an MEAI `IChatClient` durable. It can be used without the
Microsoft Agent Framework package. `TemporalCommunity.Extensions.Agents` builds on it for MAF
agent sessions, StateBag handling, context providers, routing, and agent-specific workflows.

The packages share session entries, approval contracts, the base workflow loop, and the data
converter. They keep their activity implementations separate because their execution models are
different: managed MEAI sessions run model steps and registered functions; MAF sessions run agent
steps and must preserve MAF session and provider state.

## Managed MEAI sessions own the tool loop

There is one managed-session tool model:

1. Register functions with `AddDurableTools` on every worker serving the session task queue.
2. The workflow sends those registered functions as model-visible schemas to `GetChatStep`.
3. Each model-requested function call becomes an `InvokeFunction` activity.
4. The workflow feeds function results back to subsequent model steps until it receives a final
   assistant response.

`ChatOptions.Tools` is rejected at `DurableChatSessionClient.SendAsync`, and the session's chat
client must not use `UseFunctionInvocation()`. This prevents caller-local delegates or middleware
from bypassing the workflow-owned activity boundary.

The direct `DurableChatClient` and `AIFunction.AsDurable()` adapters are separate APIs. They are
useful when application workflow code explicitly invokes a model or known function; they do not
provide caller-selected tools for a managed chat session.

## Approval and tool safety

Tool retries, timeouts, interceptors, and approval requirements belong at registration time.
Write-style tools should be idempotent or use `NoRetry()`. `RequireApproval()` makes the workflow
wait before dispatching the tool activity; it is not implemented by suspending a running tool
activity.

## Microsoft Agent Framework scope

The Agents package currently builds durable `ChatClientAgent`-shaped registrations. It does not
claim transparent support for arbitrary `AIAgent` subclasses such as A2A or graph agents.
`AIContextProvider` instructions and messages are handled in the agent execution path; provider
tools are not automatically durable and require an explicit agent-side durable registration path.

The eventual Temporal Harness profile remains a planned follow-on, not an out-of-the-box promise.

## Freeze persisted schema fingerprints

Durable declaration and toolset fingerprints are persisted deployment-drift checks. Version 1 keeps
its successful canonical output stable, including representation-sensitive JSON numbers, and maps
invalid history-carried declarations to non-retryable Temporal failures. See
[Schema fingerprint v1](./architecture/MEAI/schema-fingerprint-v1.md).
