# Option D: Phase 1 Revert — Validation Findings

**Date:** 2026-09-08  
**Status:** Phase 1 implementation reverted; architectural blockers validated  
**Commit:** `79fc58d` (Revert "feat(agents)!: Add session-owned history storage (Phase 1)")

---

## Why Phase 1 Was Reverted

Architectural review identified 10 blockers. Validation against codebase confirmed **4 critical issues** that violate project constraints and risk production correctness:

### **1. Wire Contract Violation — CRITICAL**

**Blocker:** `TemporalAgentSession` is not a source-gen serialization root  
**Evidence:** 
- CLAUDE.md line 155: "`TemporalAgentSession` is **NOT** in any source-gen context. Don't try `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))`."
- Phase 1 code (TemporalAgentSession.cs:108): `JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(typeof(TemporalAgentSession)))`
- `TemporalAgentSession` not registered in `AgentSessionJsonContext` (verified via grep)

**Risk:** Serialization falls back to reflection; loses source-gen guarantees; may silently fail during payload conversion.

---

### **2. StateBag State Corruption — CRITICAL**

**Blocker:** `_currentStateBag` remains on `TemporalAIAgent`, not session-owned  
**Evidence:**
- TemporalAIAgent.cs line 44: `private JsonElement? _currentStateBag`
- Mutations: lines 171 (passed to activity), 186 (updated from result), 429-430 (merged with tool updates)
- Multi-session scenario: Session A populates `_currentStateBag` → Session B overwrites it → Session A loses provider/tool state

**Risk:** Context providers (e.g., `WorkingSetContextProvider`) lose state across sessions; StateBag mutations disappear on continue-as-new.

---

### **3. Public API Premature Expansion — HIGH**

**Blocker:** Exposed `GetHistory()` and `HistoryEntryCount` as public API  
**Evidence:**
- TemporalAgentSession.cs lines 54, 59: both `public` 
- Added to PublicAPI.Unshipped.txt
- No separate public API review conducted

**Risk:** Exposes internal conversation content without design review; contradicts "internals hidden" claim.

---

### **4. Performance Degradation — CRITICAL**

**Blocker:** Append implementation is O(n²), not O(1)  
**Evidence:**
- TemporalAgentSession.cs lines 92-93:
  ```csharp
  var mutableHistory = new List<DurableSessionEntry>(this.History) { entry };
  this.History = mutableHistory.AsReadOnly();
  ```
- Full list copy on every append
- 100-turn workflow: 1+2+3+...+99 = ~5000 list operations

**Risk:** Unacceptable latency on multi-turn workflows; serialization payload bloat; no benchmark validation.

---

## Validation Methodology

Each blocker was validated against:
1. **Codebase inspection** — Read actual implementation files
2. **Project documentation** — CLAUDE.md constraints checked
3. **Test patterns** — Verified existing serialization patterns
4. **Execution paths** — Traced field mutations through activity lifecycle

All validation reproducible; findings are not subjective.

---

## Path Forward

**Do not proceed to Phase 2 implementation.** Instead:

### Phase 0: Wire-Contract Prototype (Architecture-Led)

1. **Design** snapshot DTO: `TemporalAgentSessionSnapshot` (internal)
   - SessionId (string)
   - StateBag (JsonElement?)
   - History (IReadOnlyList<DurableSessionEntry>)

2. **Register** in `AgentSessionJsonContext` source-gen context

3. **Implement** round-trip contract:
   - `SerializeSessionAsync()` → DTO → `JsonElement`
   - `DeserializeSessionAsync()` ← DTO ← `JsonElement`
   - Add wire-contract test asserting `JsonTypeInfo.OriginatingResolver == AgentSessionJsonContext.Default`

4. **Verify** with `TemporalAgentDataConverter` payload round-trip (polymorphic request/response/tool-content)

**Owner:** Architecture team (Morpheus/Tank/Trinity)  
**Duration:** 1-2 days  
**Gate:** Approved wire-contract design before any implementation proceeds

---

### Phase 1 (Revised): Session-Owned History + StateBag Transfer

Once wire-contract is approved:

1. Move `_currentStateBag` ownership to `TemporalAgentSession`
2. Include StateBag in snapshot DTO wire contract
3. Implement session-owned history with mutable internal storage + read-only facade
4. Define and enforce concurrent-use semantics (e.g., reject overlapping `RunAsync()` calls)
5. Validate `StateBagMerge` behavior across LLM steps and tool write-backs
6. Add specific named test gates (not "50+ tests")

**No public API exposure until separately reviewed.**

---

## Lessons Learned

1. **Validate architectural claims** — Don't accept guidance at face value; test against codebase
2. **Wire contracts are foundational** — Source-gen constraints must be checked first
3. **State ownership matters** — Multi-session scenarios need explicit ownership rules
4. **Performance claims need benchmarks** — Don't assume O(1) without data

---

## Approval Gates

**Before Phase 1 (Revised) implementation:**
- [ ] Architecture team approves wire-contract prototype design
- [ ] Concurrency rejection semantics explicitly documented
- [ ] StateBag ownership transfer validated against existing merge logic
- [ ] Test gates defined and reviewed

**This revert is intentional. The direction is sound; the implementation needs revision.**
