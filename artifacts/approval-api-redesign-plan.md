# Durable Approval API Redesign — Draft Plan

**Status:** Draft for design review. No implementation is authorized by this document.

**Scope:** A breaking pre-release redesign of the approval APIs. Implement MEAI first, then the MAF-specific scope API. This is separate from the completed `0.10.2` durable-execution baseline and does not alter its tag.

## Problem statement and source baseline

The current code has one working shared approval state machine, but its public boundary is blurred:

- `DurableApprovalDecision` in `TemporalCommunity.Extensions.AI.Approvals` contains the generic decision fields (`RequestId`, `Approved`, `Reason`) **and** MAF-only reusable-scope fields (`Scope`, `ScopePattern`). MEAI never applies those scope fields.
- `DurableChatSessionClient.ResolveApprovalAsync` returns `DurableApprovalResolutionResult`, including retry-safe `AlreadyResolved` and `Conflict` outcomes. `ITemporalAgentClient.SubmitApprovalAsync` returns only `Task`.
- `DurableApprovalMixin` already retains the most recent 32 resolved decisions and `DurableChatWorkflowBase` carries that archive through continue-as-new. This is existing behavior to preserve and document, not a new feature.
- `IDurableSessionControl` is a real current cross-library operational surface: it is implemented by `DurableChatSessionClient` and `DefaultTemporalAgentClient`, using raw Temporal workflow IDs so shared approval tooling can address either workflow kind.

The design must keep the useful shared protocol while making MAF-only approval-scope semantics owned by `TemporalCommunity.Extensions.Agents`.

## Decisions

1. Keep a shared core; do **not** create a third abstractions package.
   `TemporalCommunity.Extensions.Agents` already depends on `TemporalCommunity.Extensions.AI`, and the generic approval request/decision/resolution protocol belongs in AI.
2. The shared core decision contains only `RequestId`, `Approved`, and `Reason`.
   MEAI approval remains a per-request decision; it has no reusable scope feature.
3. MAF receives a separate, first-class `DurableAgentApprovalDecision` type in `TemporalCommunity.Extensions.Agents.Approvals`.
   It has the same core decision fields plus MAF-owned `Scope` and `ScopePattern` fields. It is not a wrapper that forces nested `Decision` construction.
4. Replace submission-only APIs with retry-safe resolve APIs.
   A successful first delivery and a retry after a lost response must be distinguishable from a conflicting decision.
5. Preserve the existing bounded retention policy: 32 resolved approvals per workflow execution chain, carried through continue-as-new. A retry outside that retained window returns `NotPending`; it is never treated as a successful retry.
6. No compatibility wrappers, deprecated overloads, wire aliases, or data migration are retained. This is a pre-release breaking change with no in-flight workflow support.

## Target public API

### Shared MEAI contract

Remain in `TemporalCommunity.Extensions.AI.Approvals`:

- `DurableApprovalRequest`
- `DurableApprovalDecision` with only `RequestId`, `Approved`, and `Reason`
- `DurableApprovalResolutionResult` and `DurableApprovalResolutionStatus`
- `DurableApprovalMeaiAdapter`

Change `IDurableSessionControl` to expose:

```csharp
Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
    string workflowId,
    DurableApprovalDecision decision,
    CancellationToken cancellationToken = default);
```

Remove its `SubmitApprovalAsync` member. `CancelPendingApprovalAsync` calls `ResolveApprovalAsync` with a generic denied decision and intentionally ignores an `AlreadyResolved` result.

`IDurableChatSessionClient.ResolveApprovalAsync` retains its current name and return type. Its behavior remains the reference generic approval contract.

### MAF-specific contract

Move these types to `TemporalCommunity.Extensions.Agents.Approvals` with the same member names and serialization shapes they have today:

- `ApprovalScope`
- `PatternMatchType`
- `ApprovalScopePattern`

Add `DurableAgentApprovalDecision` in that namespace:

```csharp
public sealed class DurableAgentApprovalDecision
{
    public required string RequestId { get; init; }
    public bool Approved { get; init; }
    public string? Reason { get; init; }
    public ApprovalScope Scope { get; init; } = ApprovalScope.ThisCallOnly;
    public ApprovalScopePattern? ScopePattern { get; init; }
}
```

Replace `ITemporalAgentClient.SubmitApprovalAsync` with:

```csharp
Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
    TemporalAgentSessionId sessionId,
    DurableAgentApprovalDecision decision,
    CancellationToken cancellationToken = default);
```

The explicit `IDurableSessionControl.ResolveApprovalAsync` implementation on `DefaultTemporalAgentClient` accepts the core MEAI decision and applies `ThisCallOnly`. This keeps generic approval dashboards reusable while making reusable scopes an intentionally MAF-typed capability.

## Implementation phases

### 0. Contract and migration record

- Create one issue per phase after this plan is approved; link the issues back here.
- Record the breaking namespace moves and method replacements in a dedicated migration document.
- Add API-baseline expectations for the new public shapes before implementation.
- Add a design test matrix that separates generic approval retries from MAF scoped-decision retries.

### 1. MEAI core extraction

- Remove `Scope` and `ScopePattern` from `DurableApprovalDecision`.
- Remove `ApprovalScope.cs` and `ApprovalScopePattern.cs` from the AI project, including their AI JSON-converter registration.
- Keep `DurableApprovalMeaiAdapter` in AI; it already converts only generic decision data. Correct its XML documentation if it references scope behavior.
- Rename the raw control operation from `SubmitApprovalAsync` to `ResolveApprovalAsync` and return `DurableApprovalResolutionResult` from both concrete implementations.
- Remove the workflow-side `[WorkflowUpdate("SubmitApproval")]` handler and its `[WorkflowUpdateValidator(nameof(SubmitApprovalAsync))]` pair from `DurableChatWorkflowBase`; do not retain a raw Temporal wire alias for the non-retry-safe path. After all callers are migrated, remove the now-orphaned `DurableApprovalMixin.SubmitApproval` and `ValidateSubmitApproval` methods as well.
- Retain the current `DurableApprovalMixin` state machine, its 32-item bounded history, and continue-as-new snapshot mechanism. Do not change its generic equivalence rules except as required by the renamed API.
- Move the current scope serialization tests out of the AI test project. MEAI tests must prove that scope types are absent from the bare MEAI package/API surface.

### 2. MAF scope-owned decision path

- Add the MAF-local scope types and `DurableAgentApprovalDecision`.
- Keep the inherited `[WorkflowUpdate("ResolveApproval")]` handler unchanged for generic dashboard resolution. Add a separate `[WorkflowUpdate("ResolveAgentApproval")]` handler for `DurableAgentApprovalDecision`; do not rely on overload discovery or runtime polymorphic JSON.
- If `ResolveAgentApprovalAsync` needs a workflow-update validator, declare it with `[WorkflowUpdateValidator(nameof(ResolveAgentApprovalAsync))]` and exercise that validator through an integration test. Never reference the generic `ResolveApprovalAsync` method name from the typed handler's validator attribute.
- Convert an accepted `DurableAgentApprovalDecision` to the generic core decision only at the `DurableApprovalMixin` boundary. The typed handler uses the mixin to accept and unblock a currently pending request, but must never return the mixin's status directly: it recomputes `Accepted`, `AlreadyResolved`, and `Conflict` from the full MAF decision ledger.
- Add a protected no-op base hook invoked only when the inherited generic `ResolveApproval` accepts a decision. `AgentWorkflow` overrides it to write the same request ID, `Approved`, and `Reason` values to the MAF ledger as an explicit `ThisCallOnly` decision with no pattern before the update returns. Only the scope fields are synthesized. This symmetric write-through means both endpoints populate both views of the shared retention window.
- Preserve the full MAF decision separately until the approval resolves, then use it for scope normalization and persistence. A successful generic decision is always `ThisCallOnly`.
- Add MAF-specific retained-resolution records to `AgentWorkflowInput` and carry them in `AgentWorkflow.CreateContinueAsNewException`.
  Full MAF decision identity—core fields plus scope and pattern—must determine `AlreadyResolved` versus `Conflict` for a typed reviewer retry.
- Treat the core mixin archive and MAF typed-decision ledger as one ordered, request-ID-keyed retention window. Evict and restore entries in lockstep, using the same 32 retained request IDs, so the generic and typed endpoints cannot disagree solely because one archive retained an entry that the other evicted.
- Keep the retention limit at 32. Both views must be bounded before serialization and restored before accepting the first post-continue-as-new resolution update.
- Update `ApprovalScopeCoordinator`, `ApprovalScopeRecord`, `ScopedApprovalInterceptor`, builder validation, and workflow persistence to use only the MAF-local scope types.
- Keep the existing scope semantics unchanged: invalid scope input degrades to `ThisCallOnly`; non-scope-aware tools do not gain reusable scope; existing StateBag/store budgets and key-collision guards remain enforced.

### 3. API cleanup and developer experience

- Remove `SubmitApprovalAsync` from `ITemporalAgentClient` and `IDurableSessionControl`; do not leave forwarding overloads.
- Keep `GetPendingApprovalAsync` returning the generic `DurableApprovalRequest`, because the request is common to both libraries.
- Update `CancelPendingApprovalAsync` on both interfaces to use the retry-safe resolve path and document its no-op/AlreadyResolved behavior.
- Make all XML documentation distinguish clearly between:
  - generic approval resolution usable by shared dashboards;
  - MEAI’s per-request-only model; and
  - MAF’s optional reusable approval scopes.
- Update `PublicAPI.Unshipped.txt` only after the intended public API is finalized; regenerate/verify API compatibility baselines as part of the build.

### 4. Documentation and samples

- Add a focused approval-concepts document defining the request lifecycle, retry outcomes, 32-decision retention limit, continue-as-new behavior, and timeout behavior.
- Update the root README and both package READMEs to state the ownership boundary: shared generic approvals; MAF-only scopes.
- Update MEAI HITL, interceptor, and adapter documentation to remove scope examples and use `ResolveApprovalAsync`.
- Update MAF HITL and approval-scope guides with `DurableAgentApprovalDecision`, including `ThisCallOnly`, `Session`, and `Always` examples.
- Update both HITL samples and any snippets that call `SubmitApprovalAsync`.
- Add a small shared-dashboard example that resolves a generic decision through `IDurableSessionControl`, explicitly showing that it cannot grant an MAF reusable scope.

### 5. Tests and verification

#### Unit tests

- MEAI: core decision serialization, MEAI adapter conversion, generic result-status behavior, 32-item eviction, and no MAF type/package dependency.
- MAF: scope/pattern serialization in the Agents package; typed decision validation; scope normalization; StateBag and always-store persistence; generic resolution defaulting to `ThisCallOnly`.
- Both: `Accepted`, `AlreadyResolved`, `Conflict`, `RequestMismatch`, and `NotPending` semantics.

#### Integration and replay tests

- Generic MEAI approval, retry after a lost response, and retry after continue-as-new.
- MAF typed approval with each scope; retry of the identical typed decision; a retry that changes scope or pattern and returns `Conflict`.
- Generic dashboard resolution against an MAF workflow through the inherited `ResolveApproval` update, proving it resolves the pending request but grants no scope.
- Cross-endpoint retry tests: generic-first then typed retry returns `AlreadyResolved` only for the equivalent explicit `ThisCallOnly` decision; typed-first then generic retry also returns `AlreadyResolved`; either route returns `Conflict` for a changed scoped decision.
- A direct raw Temporal attempt to invoke the removed `SubmitApproval` update fails as an unknown update; no SDK documentation, sample, or test retains that wire name.
- Typed `ResolveAgentApproval` update wiring, including its validator when present, is exercised through an integration test rather than inferred from a method overload.
- Retention-boundary tests that prove generic and typed MAF resolution agree on `AlreadyResolved` versus `NotPending` before and after a shared archive eviction.
- Timeout, cancellation, delayed duplicate resolution, and a resolution beyond the 32-record retention window.
- Replay and continue-as-new tests that confirm the MAF scope record and the retained typed-resolution record survive exactly as specified.

#### Package, documentation, and sample gates

- `just test`
- `just smoke-downlevel-proxy`
- `just test-samples-meai`
- `just test-samples-maf`
- `just verify-sample-coverage`
- local Markdown-link validation and `git diff --check`
- GitHub Actions Build, Integration Tests, and CodeQL green for the implementation commit.

## Explicit non-goals

- No `HarnessAgent` support or Temporal-native harness profile work.
- No support for arbitrary `AIAgent`, A2A, streaming, external history, or caller-owned tool execution.
- No new package solely to host approval abstractions.
- No compatibility support for the `0.10.2` approval surface or workflows started by it.

## Completion criteria

The redesign is complete only when the shared and MAF-specific APIs compile without cross-ownership leakage, every affected README/document/sample uses the new APIs, all listed local checks pass, and the implementation commit has green Build, Integration Tests, and CodeQL runs.
