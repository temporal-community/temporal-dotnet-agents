# Generalized deferred tools: research decision

Status: **Defer**. This document records a non-shipping design investigation. It does not define a
public API, workflow type, Update, Query, payload contract, or promised future feature.

## Question

Should `TemporalCommunity.Extensions.AI` generalize its durable approval wait into a mechanism that
lets a tool declare missing external input, park workflow orchestration without holding a worker
thread, accept the input later, and resume the tool?

This is distinct from approval. Approval resolves a fixed yes/no policy decision for an already
formed invocation. General deferred input needs an application-defined input kind and payload,
capacity and backpressure rules, duplicate/conflict semantics, cancellation and timeout behavior,
and a safe point at which current authorization is evaluated.

## Existing approval implementation

`DurableApprovalMixin` is intentionally approval-specific and internal. It provides useful
evidence, but it is not a reusable deferred-work coordinator:

- it permits one pending approval and parks through `Workflow.WaitConditionAsync`;
- it accepts retry-safe resolutions and distinguishes accepted, already-resolved, conflicting,
  mismatched, and not-pending decisions;
- it retains the last 32 resolutions across Continue-as-New; and
- its request, decision, timeout message, and public workflow handlers all encode approval
  semantics.

Extracting that class into a generic public primitive would expose workflow history and Update
contracts before the missing-input behavior is settled. Keeping a separate internal coordinator
would avoid semantic overloading, but still requires the contracts below.

## Alternatives considered

### Public discriminated pending-work contract

A single union could represent approvals, missing credentials, operator input, asynchronous jobs,
and other waits. It is expressive, but every case and discriminator becomes a permanent Temporal
wire contract. Consumers would also need type-safe payload conversion and versioning rules. This is
too broad without multiple proven application cases.

### Package coordinator plus application adapter

The package could own pending IDs, timeouts, duplicate handling, Continue-as-New state, and Queries,
while an application adapter maps typed input to a resumed tool. This is the most plausible future
direction. The test-only prototype evaluates its core state machine, but does not settle payload
validation, authorization ownership, observability, or a public typed API.

### Application-owned workflow composition

Applications can model a missing-input wait today with their own workflow state and an Update or
Signal. This costs application code but keeps domain payloads and policy local. It is the recommended
choice until a package contract meets the adoption gates below.

## Test-only prototype findings

The prototype is compiled only into `TemporalCommunity.Extensions.Tests.Shared` and the test
projects. It is not referenced by production code and cannot enter a NuGet package.

The executable research establishes:

- the default pending-input cap can be one and cap pressure is deterministic;
- identical request/completion retries are idempotent while changed definitions or payloads
  conflict;
- timeout and cancellation are terminal, so late completion cannot revive an invocation;
- pending input and bounded resolution history can be snapshotted and restored;
- a pending request survives a real Continue-as-New, and both resulting histories replay;
- restored capacity continues to apply after Continue-as-New; and
- authorization can be evaluated again immediately before each resumed effect attempt rather than
  serialized with the pending input.

The prototype deliberately does not claim exactly-once effects. A resumed external effect must use
an application idempotency key. It also does not treat submitted input, workflow state, or an earlier
authorization result as current authorization.

## Decision

**Defer** a shipping implementation. The research validates that the workflow state machine is
feasible, but one internal prototype and one current consumer category are not enough evidence for
a durable public contract.

No deferred-tool research type may be added to a production project, `PublicAPI.*.txt`, the package
asset graph, or a registered workflow/update/query name while this decision remains `Defer`.

## Required exit criteria for a future ADR

A future proposal must end in an explicit **Adopt**, **Defer**, or **Reject** decision and must not
publish API before **Adopt**. Adoption requires all of the following:

1. At least two concrete, non-approval use cases with compatible payload, timeout, cancellation,
   and observability requirements.
2. Exact public types, enum numeric values, serialized-field policy, stable identifier algorithm,
   and versioning rules.
3. A normative state-transition table covering request, duplicate, conflict, completion, timeout,
   cancellation, retry, and capacity behavior.
4. Deterministic bounded-cap behavior, including whether ordered concurrency greater than one is
   required and how resource pressure is reported.
5. Update validation and terminal application failures for malformed or oversized input.
6. Authorization immediately before the resumed effect using authoritative current data; tenant
   payloads and prior decisions are not authorization grants.
7. Activity/workflow retry and idempotency guidance that makes no exactly-once promise.
8. Real restart, Continue-as-New, replay, cancellation-race, timeout-race, and conflicting-retry
   integration coverage.
9. A domain-neutral end-to-end sample and operational documentation.

Until those gates are met, application-owned workflow composition is the supported approach.

MCP Tasks provide a second concrete deferred-work protocol, but the current investigation also ends
at **Defer** because durable Task creation and server execution ownership are unresolved. See the
[MCP Task research ADR](../MCP/durable-mcp-task-research.md).
