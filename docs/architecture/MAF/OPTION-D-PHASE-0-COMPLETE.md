# Option D: Phase 0 Specification Complete — Ready for Phase 1 (Revised)

**Status:** ✅ APPROVED  
**Date:** 2026-09-08  
**Approval:** Architecture Team (Morpheus/Tank/Trinity)  
**Deliverable:** Approved wire-contract specification and Phase 1 validation gates

---

## Summary

Phase 0 defined the serialization contract for session-owned state across Temporal workflow
boundaries. The specification was reviewed, corrected, and approved. Phase 0 is design work: it
does not claim that the snapshot DTO, session-owned history, or round-trip implementation has
already landed in production code.

**Result:** Phase 1 (Revised) implementation is now unblocked.

---

## What Phase 0 Delivered

### ✅ Wire-Contract Design
- **TemporalAgentSessionSnapshot** — Internal DTO specified for source-gen registration
- **Round-trip methods** — `SerializeSessionCoreAsync()` / `DeserializeSessionCoreAsync()` behavior specified
- **Constraint compliance** — Respects CLAUDE.md line 155 (no direct session serialization)
- **Payload efficiency** — Null fields omitted from JSON

### ✅ Validation Gates Defined
1. **Source-Gen Resolver-Origin** — Verifies snapshot registration is selected ahead of reflection
2. **Polymorphic Payload Round-Trip** — Validates request/response/tool-content serialization via `TemporalAgentDataConverter`
3. **Legacy Snapshot Compatibility** — A persisted snapshot without `history` decodes as empty history
4. **Empty State Optimization** — Null/empty fields are omitted
5. **Agent Serialization Boundary (Phase 1)** — Validates typed StateBag and history restoration through the public MAF session API, including a legacy snapshot without `history`

Gates 1–4 must be implemented and pass with the wire-contract prototype before the session-owned
history work is accepted. Gate 5, the same-live-session rejection test, and the distinct-session
isolation test are Phase 1 acceptance tests.

### ✅ Architecture Decisions
| Decision | Outcome | Rationale |
|----------|---------|-----------|
| **Concurrent-use semantics** | Reject overlapping calls immediately | Fail-fast, simple, aligns with session ownership |
| **StateBag update flow** | Overlay trusted LLM output after each LLM step; merge tool/interceptor write-backs after complete fan-out in tool-call index order | Preserves deterministic state and avoids completion-order races |
| **History truncation** | Keep full history always | Compaction is Phase 2+ work (deferred) |
| **Version field** | Not yet (defer until migration exists) | No defined migration scenario yet |

### ✅ Implementation Clarity
- Code examples provided (snapshot DTO, round-trip logic, test gates)
- Customer workflow pattern documented (explicit carry-forward, no magic)
- Clear handoff to Phase 1 team

---

## Architecture Review Process

**Timeline:**
- Phase 1 launched with contract violation → reverted (2026-09-08)
- 4 critical issues validated against codebase
- Phase 0 specification drafted
- Architecture team reviewed
- 2 clarifications applied
- Specification APPROVED

**Validation Methodology:**
✅ Technical soundness verified  
✅ Constraint alignment confirmed (CLAUDE.md)  
✅ Test gates deemed comprehensive  
✅ Open questions answered with rationale  

---

## Corrected Per Review

### Clarification 1: SessionId Preservation
- **Change:** Persist the complete workflow ID using `temporalSession.SessionId.WorkflowId`
- **Impact:** Preserves the lossless session identity required for restoration
- **Scope:** This specification does not introduce agent-name validation. Any such validation requires a separately defined behavior and regression test.

### Clarification 2: Gate 2 Enhancement
- **Change:** Expanded polymorphic payload test with tool-call cycle
- **Impact:** Validates FunctionCallContent/FunctionResultContent round-trip
- **Coverage:** Request → Tool Call → Result flow

---

## Phase 1 (Revised) Unblocked

Implementation team can now proceed with:

1. Implement `TemporalAgentSessionSnapshot` and register it in `AgentSessionJsonContext`
2. Make `TemporalAIAgent.SerializeSessionCoreAsync()` / `DeserializeSessionCoreAsync()` use the snapshot contract
3. Implement and pass Gates 1–4, then Gate 5 with session-owned history
4. Add concurrent-use rejection and distinct-session isolation tests
5. Move `_currentStateBag` to session ownership using the specified LLM overlay and ordered tool/interceptor merge rules
6. Add session-owned history with internal mutable storage
7. Benchmark at 1, 10, and 100 turns
8. Validate explicit continue-as-new carry-forward end to end

**Estimated Duration:** 5-8 engineer-days  
**Critical Path:** Wire contract (complete) → Session ownership (2-3 days) → Integration tests (2-3 days)

---

## Architectural Accomplishments

✅ **Constraint Compliance** — The specification requires source-gen serialization rather than direct session serialization

✅ **State Ownership** — The target session ownership model and concurrency contract are defined

✅ **Backward Compatibility** — The required legacy snapshot behavior is defined and gated

✅ **Payload Efficiency** — The required null-field omission behavior is defined and gated

✅ **Determinism** — The required resolver-origin and StateBag merge behavior are defined and gated
✅ **Clarity** — The implementation team has a bounded, testable handoff

---

## Lessons Learned

1. **Validate architectural claims** against actual codebase constraints
2. **Wire contracts are foundational** — get them right before implementation
3. **Test gates prevent hidden failures** — define tests before code
4. **Source-gen compliance is non-negotiable** — CLAUDE.md constraints are load-bearing
5. **Architecture review catches critical issues** — revert + redesign is faster than shipping broken code

---

## Sign-Off

**Phase 0 Status:** ✅ Specification complete

**Architecture Approval:** ✅ Approved specification

**Phase 1 Ready:** ✅ Yes — implementation and its acceptance tests remain outstanding

**Next Step:** Implement Phase 1 (Revised) per specification with all test gates defined upfront.

**Documentation:** All specifications, blockers, and rationale preserved in:
- `OPTION-D-PHASE-0-WIRE-CONTRACT.md` — Implementation specification (APPROVED)
- `OPTION-D-REVISION-BLOCKERS.md` — Original blocker analysis
- `OPTION-D-REVERT-RATIONALE.md` — Why Phase 1 was reverted + validation methodology

---

**Approved by:** Architecture Team (Morpheus/Tank/Trinity)  
**Date:** 2026-09-08  
**Status:** READY FOR PHASE 1 (REVISED) IMPLEMENTATION
