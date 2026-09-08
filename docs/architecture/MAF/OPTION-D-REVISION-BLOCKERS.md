# Option D: Phase 2+ Blockers — Architectural Revision Required

**Status:** Phase 1 (foundation) committed; Phase 2+ blocked pending design review  
**Date:** 2026-09-08  
**Blocker Owner:** Architecture team (Morpheus/Tank/Trinity)

---

## Critical Issues Identified

### 1. Wire Contract Safety — **BLOCKER**

**Current Problem:**  
Phase 1 serializes `TemporalAgentSession` directly in workflow input carry-forward, but [CLAUDE.md's JSON serialization guidance](../../../CLAUDE.md#json-serialization-gotchas) explicitly states `TemporalAgentSession` is NOT a serialization root.

**Required Fix:**
- Define an internal, source-generated snapshot DTO: `TemporalAgentSessionSnapshot` containing:
  - SessionId (`string`)
  - StateBag (`JsonElement?`)
  - History (IReadOnlyList<DurableSessionEntry>)
- Add to `AgentSessionJsonContext` source-gen context
- Customer workflows use `SerializeSessionAsync()` → `JsonElement` → `DeserializeSessionAsync()` for carry-forward (not direct `TemporalAgentSession`)
- The snapshot remains an implementation detail; customer workflow inputs carry only the returned `JsonElement`.
- Add a wire-contract test that asserts both `JsonTypeInfo.OriginatingResolver == AgentSessionJsonContext.Default` and a real `TemporalAgentDataConverter` payload round-trip preserves StateBag, derived request/response entries, and tool content.
- Treat a missing `history` field as the documented legacy payload shape. Add a version field only when a future migration has a defined versioned behavior.

**Impact:** Phase 2 cannot proceed without this contract.

---

### 2. StateBag Ownership Incomplete — **BLOCKER**

**Current Problem:**  
History moved to session, but [`_currentStateBag`](../../../src/TemporalCommunity.Extensions.Agents/TemporalAIAgent.cs#L44) remains on `TemporalAIAgent`. Multi-session scenario:
- Session A runs, populates `_currentStateBag`
- Session B runs, overwrites `_currentStateBag`
- Session A has lost provider/tool state, context providers are out of sync
- Continued workflow after continue-as-new loses all StateBag mutations

**Required Fix:**
- Move the serialized StateBag transport to `TemporalAgentSession`, whose inherited MAF `StateBag` remains the single authoritative StateBag model. Do not introduce a second StateBag model.
- Add an internal session operation that replaces/applies activity-produced serialized StateBag values before the next activity dispatch.
- Include `StateBag` in session wire contract (snapshot DTO)
- Preserve existing deterministic `StateBagMerge` behavior across LLM steps and tool write-backs
- Test: StateBag mutations visible across concurrent distinct sessions, mutations not lost on continue-as-new

**Impact:** Without this, multi-session isolation is broken; workflow continuation loses state.

---

### 3. Premature Public API Expansion — **BLOCKER**

**Current Problem:**  
Phase 1 adds `GetHistory()` and `HistoryEntryCount` as public API. But:
- They expose internal conversation content and compaction markers
- Plan claims "internals are hidden" — contradicted by public history access
- No separate public history API review has been conducted

**Required Fix:**
- Remove `GetHistory()` and `HistoryEntryCount` from public surface
- Keep history internal to session and TemporalAIAgent
- If public history API is needed later, design and review separately

**Impact:** Reduces public API surface, removes unreviewed surface area.

---

### 4. Compaction Model Unwired — **BLOCKER**

**Current Problem:**  
Phase 1 adds `SessionHistoryMetadata` and `SetHistory()` but:
- No compaction trigger logic implemented
- Unclear when/how compaction markers are inserted
- Introduces a second, parallel compaction model alongside future history reduction
- Creates public/internal API confusion (when is customer allowed to call `SetHistory()`?)

**Required Fix:**
- Remove `SessionHistoryMetadata` from Phase 1
- Remove `SetHistory()` from this scope. It is already internal, but it is unnecessary until separately authorized history reduction work exists.
- Defer history compaction/reduction to separate Phase N work with full pair-boundary invariants

**Impact:** Simplifies Phase 1 scope; clarifies separation of concerns.

---

### 5. Concurrent-Use Semantics Undefined — **BLOCKER**

**Current Problem:**  
No contract defined for concurrent access. What happens if:
- Two overlapping workflow tasks call `RunAsync()` on the same live `TemporalAgentSession`?
- First call appends request, second call appends request → who owns the ordering?
- Workflow has overlapping sub-agent calls via `GetTemporalAgent()`?

**Required Fix:**
- Define explicit concurrency rule:
  - **Recommended:** `RunAsync()` calls on the same live session reject overlap with a clear `InvalidOperationException`; callers use distinct session objects for parallel conversations.
  - **Option B:** `RunAsync()` calls are serialized internally; history appends are ordered by causality
- Add a gate test that asserts the documented rejection behavior for overlapping same-session runs.

**Impact:** Prevents subtle race conditions in production workflows.

---

### 6. Performance Claim Incorrect — **BLOCKER**

**Current Problem:**  
Plan claims O(1) append: "Lazy-load history only when needed." Actual implementation:
- `AppendHistoryEntry()` copies entire `List<>`, creates new `AsReadOnly()` wrapper → **O(n)**
- Full history transmitted on every LLM step in `RunCoreAsync` → **O(n) per turn**
- No benchmarking at 1, 10, 100-turn scales

**Required Fix:**
- Use mutable internal storage (`List<DurableSessionEntry>`) with read-only `IReadOnlyList<>` facade
- Benchmark append performance: 1-turn, 10-turn, 100-turn workflows
- Verify serialization payload size and CAN weight don't regress
- Document performance characteristics in implementation

**Impact:** Prevents unexpected latency spikes in production; justifies no-regression claim.

---

### 7. Validation Inconsistency — **BLOCKER**

**Current Problem:**  
Plan defines new validation exception for agent-name mismatch. But [`TemporalAIAgentProxy.ValidateSessionOwnership`](../../../src/TemporalCommunity.Extensions.Agents/TemporalAIAgentProxy.cs#L169) uses `ArgumentException` with `paramName: "session"`.

**Required Fix:**
- Match proxy validation: throw `ArgumentException` when session agent name doesn't match
- Message: `"The provided session belongs to agent '{sessionId.AgentName}', not agent '{agentName}'."`
- Consistent exception type and parameter naming across proxy and agent

**Impact:** Unified error handling; easier for callers to catch and handle.

---

### 8. Test Gates Too Vague — **BLOCKER**

**Current Problem:**  
Plan specifies "50+ unit/integration tests" without concrete test gates. No explicit coverage for:
- Source-gen snapshot resolver-origin and round-trip
- `TemporalAgentDataConverter` payload round-trip for polymorphic request/response/tool-content serialization
- Legacy payloads without a `history` field
- Concurrent distinct sessions (isolation)
- Overlapping same-session calls (concurrency rule)
- StateBag mutation through LLM + tool write-backs
- Fresh agent restore from serialized snapshot
- Workflow continue-as-new with snapshot carry-forward

**Required Fix:**
- Define named test gates (listed above)
- Each gate is a specific test class/method with clear pass criteria
- Define these named gates before implementation, then add each focused unit test with its corresponding wire-contract or behavior change. Do not defer the complete test suite until after the migration.

**Impact:** Ensures test coverage is complete and intentional, not generic.

---

### 9. Architecture Docs Incomplete — **BLOCKER**

**Current Problem:**  
Plan only mentions migration guide. But existing docs conflict with the new design:
- [session-statebag-and-context-providers.md](./session-statebag-and-context-providers.md#temporalaiaagent--history-is-on-the-instance-not-the-session) — explains the current instance-owned history and StateBag model
- [agent-to-agent-communication.md](./agent-to-agent-communication.md#history-accumulation) — assumes agent-wide history
- [durability-and-determinism.md](./durability-and-determinism.md#path-b--orchestrating-workflow--sub-agent-via-temporalaiaagent) — defines the internal-agent workflow path

**Required Fix:**
- Update all three architecture docs with new session ownership model
- Update CLAUDE.md session isolation rules
- Add explicit note: "Sessions own history and StateBag; multi-session scenarios require distinct session objects"
- Update dos-and-donts.md with carry-forward pattern

**Impact:** Documentation stays in sync with implementation; prevents confusion for future readers.

---

### 10. Release & Publish Out of Scope

**Current Problem:**  
Plan includes "Phase 5: Release" with version bump and NuGet publish. That requires separate approval and should not be in this implementation scope.

**Required Fix:**
- Remove release/publish from this plan
- After implementation + verification, file separate "Release v0.4.0" work
- Version bump and publish require explicit team approval

**Impact:** Clarifies scope boundaries; decouples implementation from release decisions.

---

## Recommended Revision Order

1. **Wire-contract prototype and focused tests** (1-2 days)
   - Design snapshot DTO
   - Add to source-gen context
   - Add resolver-origin, data-converter payload, polymorphic-content, and legacy-payload tests

2. **Session-owned history + StateBag transfer and focused tests** (2-3 days)
   - Move both to session
   - Define concurrency rule and enforcement
   - Add isolation, StateBag write-back, validation, and same-session-overlap tests

3. **Agent migration and performance evidence** (2-3 days)
   - Remove `_history` from TemporalAIAgent
   - Use session-owned storage
   - Add performance benchmarks

4. **Integration/replay/continue-as-new tests** (2-3 days)
   - Full workflow carry-forward contract
   - StateBag mutation through LLM + tools
   - Legacy payload compatibility

5. **Documentation updates** (1-2 days)
   - Revise all architecture docs
   - Update CLAUDE.md and dos-and-donts.md

---

## Approval Gate

**Before Phase 2 implementation begins:**
- [ ] Architecture team approves wire-contract design
- [ ] Concurrency rule is explicit and documented
- [ ] Test gate list is complete
- [ ] Documentation plan is reviewed

**This plan is ready for revision. Do not proceed to Phase 2 until blockers are resolved.**
