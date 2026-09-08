# Option D: Phase 2+ Blockers — Architectural Revision Required

**Status:** Phase 1 (foundation) committed; Phase 2+ blocked pending design review  
**Date:** 2026-09-08  
**Blocker Owner:** Architecture team (Morpheus/Tank/Trinity)

---

## Critical Issues Identified

### 1. Wire Contract Safety — **BLOCKER**

**Current Problem:**  
Phase 1 serializes `TemporalAgentSession` directly in workflow input carry-forward, but [CLAUDE.md line 153](../../CLAUDE.md#key-type-locations-gotchas) explicitly states `TemporalAgentSession` is NOT a serialization root.

**Required Fix:**
- Define an internal source-gen snapshot DTO: `TemporalAgentSessionSnapshot` containing:
  - SessionId (string)
  - StateBag (JsonElement)
  - History (IReadOnlyList<DurableSessionEntry>)
- Add to `AgentSessionJsonContext` source-gen context
- Customer workflows use `SerializeSessionAsync()` → `JsonElement` → `DeserializeSessionAsync()` for carry-forward (not direct `TemporalAgentSession`)
- Add polymorphic round-trip test: serialize/deserialize request/response/tool-content through snapshot

**Impact:** Phase 2 cannot proceed without this contract.

---

### 2. StateBag Ownership Incomplete — **BLOCKER**

**Current Problem:**  
History moved to session, but `_currentStateBag` remains on `TemporalAIAgent` (line 37). Multi-session scenario:
- Session A runs, populates `_currentStateBag`
- Session B runs, overwrites `_currentStateBag`
- Session A has lost provider/tool state, context providers are out of sync
- Continued workflow after continue-as-new loses all StateBag mutations

**Required Fix:**
- Move `_currentStateBag` ownership to `TemporalAgentSession`
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
- Remove public `SetHistory()` (keep as internal-only if needed)
- Defer history compaction/reduction to separate Phase N work with full pair-boundary invariants

**Impact:** Simplifies Phase 1 scope; clarifies separation of concerns.

---

### 5. Concurrent-Use Semantics Undefined — **BLOCKER**

**Current Problem:**  
No contract defined for concurrent access. What happens if:
- Two threads call `RunAsync()` on the same `TemporalAgentSession` simultaneously?
- First call appends request, second call appends request → who owns the ordering?
- Workflow has overlapping sub-agent calls via `GetTemporalAgent()`?

**Required Fix:**
- Define explicit concurrency rule:
  - **Option A:** `RunAsync()` calls on same session must be serial (reject overlapping calls)
  - **Option B:** `RunAsync()` calls are serialized internally; history appends are ordered by causality
  - **Option C:** Sessions are single-use; new session per concurrent call
- Add gate test: overlapping same-session behavior (verify rejection or serialization)

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
Plan defines new validation exception for agent-name mismatch. But [TemporalAIAgentProxy line 169](../../src/TemporalCommunity.Extensions.Agents/TemporalAIAgentProxy.cs#L169) uses `ArgumentException` with `paramName: "session"`.

**Required Fix:**
- Match proxy validation: throw `ArgumentException` when session agent name doesn't match
- Message: `"The provided session belongs to agent '{sessionId.AgentName}', not agent '{agentName}'."`
- Consistent exception type and parameter naming across proxy and agent

**Impact:** Unified error handling; easier for callers to catch and handle.

---

### 8. Test Gates Too Vague — **BLOCKER**

**Current Problem:**  
Plan specifies "50+ unit/integration tests" without concrete test gates. No explicit coverage for:
- Source-gen snapshot round-trip
- Polymorphic request/response/tool-content serialization
- Legacy payloads (v0.2 without history)
- Concurrent distinct sessions (isolation)
- Overlapping same-session calls (concurrency rule)
- StateBag mutation through LLM + tool write-backs
- Fresh agent restore from serialized snapshot
- Workflow continue-as-new with snapshot carry-forward

**Required Fix:**
- Define named test gates (listed above)
- Each gate is a specific test class/method with clear pass criteria
- Add to Phase 3 (testing) specification before implementation

**Impact:** Ensures test coverage is complete and intentional, not generic.

---

### 9. Architecture Docs Incomplete — **BLOCKER**

**Current Problem:**  
Plan only mentions migration guide. But existing docs conflict with new design:
- [session-statebag-and-context-providers.md line 384](../../src/TemporalCommunity.Extensions.Agents/Session/TemporalAgentSession.cs#L384) — explains current session/StateBag model
- [agent-to-agent-communication.md line 76](../../src/TemporalCommunity.Extensions.Agents/TemporalAIAgentProxy.cs#L76) — assumes agent-wide state
- [durability-and-determinism.md line 163](../MAF/durability-and-determinism.md#L163) — session semantics

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

1. **Wire-contract prototype** (1-2 days)
   - Design snapshot DTO
   - Add to source-gen context
   - Round-trip test (serialize/deserialize session)

2. **Session-owned history + StateBag transfer** (2-3 days)
   - Move both to session
   - Define concurrency rule and enforcement
   - Add validation gate tests

3. **Agent migration** (2-3 days)
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
