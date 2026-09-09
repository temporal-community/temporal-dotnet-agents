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

`AIFunction.AsDurable()` is a separate, lower-level API: it lets application workflow code
explicitly invoke a known function without going through a managed chat session, and does not
provide caller-selected tools for a managed chat session. Constructing `DurableChatClient` (or any
`ChatClientBuilder` composition around it) directly inside workflow code is a retired anti-pattern —
see [Direct-adapter-in-workflow anti-pattern](./architecture/MEAI/direct-adapter-anti-pattern.md)
for the full rationale and the supported alternatives.

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

## MAF durable agents reject in-process function invocation

A `FunctionInvokingChatClient` anywhere in the chat client returned from `agent.ChatClient` is
rejected. The activity fails non-retryably before the model is called.

`AddDurableAgent` always dispatches tool calls as separate `InvokeAgentTool` activities — that is
the contract, and `ChatClientAgent` is built with `UseProvidedChatClientAsIs = true` to stop MAF
installing its own loop. An in-process loop consumes the tool call first, so per-tool retry
policies, `NoRetry()`, per-tool timeouts, and event-history visibility all stop applying while the
agent keeps answering. The failure is silent and inverted: the more carefully per-tool policy was
configured, the more is lost.

**The rule is unconditional — there is no "agent has no tools" exemption.** An empty
`ChatOptions.Tools` does not make the middleware inert: `FunctionInvokingChatClient.AdditionalTools`
is consulted when the inner client requests a tool that was not sent on the request, so a tool-less
registration can still execute functions in-process. Gating on registered tool count would have to
reason about that mutable collection too, for no benefit over one rule.

**Detection is positive structural detection, not a closed door.** The chain is walked through
`DelegatingChatClient.InnerClient`, then `GetService(typeof(FunctionInvokingChatClient))` is
consulted — a non-null result is treated as evidence the client participates in the effective
pipeline. Since `FunctionInvokingChatClient` derives from `DelegatingChatClient`, any conventional
chain is found by one path or the other. A wrapper that neither derives from `DelegatingChatClient`
nor forwards `GetService` remains undetectable, so the guard backs the documented rule rather than
replacing it.

**No opt-out is offered.** Positive detection means a real in-process loop, not a speculative false
positive. The supported configuration for code that genuinely needs in-process invocation is a
separate decorated `IChatClient` for that non-durable work, with an undecorated client given to the
durable agent.

The MEAI library takes the opposite position for its own Pattern 1: a session with no durable tools
registered may use `FunctionInvokingChatClient`, because the caller has explicitly chosen the
in-process loop and no durability was promised. The divergence is intentional.

## Freeze persisted schema fingerprints

Durable declaration and toolset fingerprints are persisted deployment-drift checks. Version 1 keeps
its successful canonical output stable, including representation-sensitive JSON numbers, and maps
invalid history-carried declarations to non-retryable Temporal failures. See
[Schema fingerprint v1](./architecture/MEAI/schema-fingerprint-v1.md).
