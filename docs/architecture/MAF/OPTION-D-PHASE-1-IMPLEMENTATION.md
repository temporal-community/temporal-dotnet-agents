# Option D: Phase 1 (Revised) — Implementation Specification

**Status:** Ready for Implementation  
**Owner:** Development Team  
**Scope:** Session-owned history + StateBag transfer + wire-contract validation  
**Estimated Duration:** 5-8 engineer-days  
**Start Date:** 2026-09-08  
**Acceptance Criteria:** All test gates passing + benchmarks < threshold + no regressions

---

## Overview

Phase 1 implements the wire-contract designed in Phase 0. Work is decomposed into 5 sequential phases with parallel opportunities.

**Outcome:** TemporalAIAgent uses session-owned state; multi-session isolation guaranteed; continue-as-new carries full state; all tests passing.

---

## Phase 1a: Wire-Contract Prototype (Days 1-2)

### Deliverables

1. **TemporalAgentSessionSnapshot.cs** (new file)
   ```csharp
   internal sealed class TemporalAgentSessionSnapshot
   {
       public required string SessionId { get; init; }
       public JsonElement? StateBag { get; init; }
       public IReadOnlyList<DurableSessionEntry>? History { get; init; }
   }
   ```

2. **Register in AgentSessionJsonContext**
   ```csharp
   [JsonSerializable(typeof(TemporalAgentSessionSnapshot))]
   ```

3. **Round-trip methods in TemporalAIAgent**
   - `SerializeSessionCoreAsync()` → builds snapshot → serializes via source-gen
   - `DeserializeSessionCoreAsync()` → deserializes snapshot → restores session

4. **Implement Prototype Test Gates 1-4**
   - Gate 1: Source-gen resolver-origin
   - Gate 2: Polymorphic payload round-trip (with tool-call cycle)
   - Gate 3: Legacy payload compatibility (no history field)
   - Gate 4: Empty state optimization

### Success Criteria

- [ ] TemporalAgentSessionSnapshot compiles and is source-gen registered
- [ ] Round-trip methods compile without reflection fallback
- [ ] All 4 prototype gates pass
- [ ] No regressions in existing serialization tests
- [ ] CI/CD builds clean (no warnings)

### Notes

- Do NOT add session-owned history mutations yet (AppendHistoryEntry, SetHistory)
- Do NOT move `_currentStateBag` to session ownership yet
- Snapshot DTO is internal; no public API exposed
- Use exact code from Phase 0 specification

---

## Phase 1b: Session-Owned History Foundation (Days 2-3)

### Deliverables

1. **Add history fields to TemporalAgentSession**
   ```csharp
   internal IReadOnlyList<DurableSessionEntry> History { get; private set; } = [];
   internal void AppendHistoryEntry(DurableSessionEntry entry) { ... }
   internal void SetHistory(IReadOnlyList<DurableSessionEntry> newHistory) { ... }
   ```

2. **Update snapshot DTO constructor** to accept history parameter
   ```csharp
   internal TemporalAgentSession(
       TemporalAgentSessionId sessionId,
       AgentSessionStateBag stateBag,
       IReadOnlyList<DurableSessionEntry>? history = null)
   ```

3. **Update round-trip methods**
   - Serialization: Include history in snapshot
   - Deserialization: Restore history via AppendHistoryEntry

4. **Implement Gate 5: Agent Serialization Boundary**
   - Validates typed StateBag and history restoration through public MAF session API
   - Tests legacy snapshot without history

### Success Criteria

- [ ] TemporalAgentSession owns history (internal field)
- [ ] Gate 5 (serialization boundary) passes
- [ ] Legacy snapshots deserialize with empty history
- [ ] Round-trip preserves history entries in order
- [ ] No regressions in existing session tests

### Notes

- History is still read-only to callers (no public GetHistory yet)
- Do NOT modify TemporalAIAgent._history field yet
- Focus on session ownership contract first
- Keep AppendHistoryEntry O(1) with mutable internal storage + read-only facade

---

## Phase 1c: StateBag Transfer to Session (Days 3-4)

### Deliverables

1. **Move _currentStateBag ownership to TemporalAgentSession**
   - Add internal field: `private JsonElement? _currentStateBag`
   - Add internal method: `void ApplyStateBagUpdate(JsonElement update)`
   - Remove `_currentStateBag` from TemporalAIAgent

2. **Update round-trip contract**
   - Serialize: Include StateBag in snapshot
   - Deserialize: Restore StateBag to session

3. **Implement StateBag merge rules** (determinism-critical)
   - After LLM step: Overlay trusted LLM output
   - After tool fan-out: Merge tool/interceptor write-backs in tool-call index order
   - Document merge semantics in code comments

4. **Update TemporalAIAgent.RunCoreAsync**
   - Remove local `_currentStateBag` mutations
   - Use `session.ApplyStateBagUpdate()` instead
   - Preserve existing `StateBagMerge` logic (order-independent result)

### Success Criteria

- [ ] StateBag owned by session (not agent)
- [ ] Multi-session test: Two sessions on same agent have isolated StateBag
- [ ] Continue-as-new: StateBag mutations preserved across CAN boundary
- [ ] StateBag merge is deterministic (order-independent result)
- [ ] No regressions in context-provider tests (WorkingSetContextProvider, etc.)

### Notes

- StateBag ordering matters: LLM output first, then tool/interceptor merge
- Document why: Ensures deterministic, replay-safe state
- This is the most critical phase for correctness

---

## Phase 1d: TemporalAIAgent Migration (Days 4-5)

### Deliverables

1. **Remove instance-level history from TemporalAIAgent**
   - Delete: `private readonly List<DurableSessionEntry> _history = []`
   - All history reads/writes now go through session

2. **Update RunCoreAsync to use session-owned history**
   - Replace `_history.Add()` → `session.AppendHistoryEntry()`
   - Replace `foreach (var entry in _history)` → `foreach (var entry in session.History)`

3. **Add concurrent-use rejection validation**
   ```csharp
   if (_isRunning)
       throw new InvalidOperationException(
           "Overlapping RunAsync() calls on same session are not allowed. " +
           "Use distinct session objects for parallel conversations.");
   ```

4. **Implement Gate 5 continuation: Concurrency tests**
   - Same-session overlap rejection test
   - Distinct-session isolation test
   - Parallel runs with different sessions pass

### Success Criteria

- [ ] No instance-level `_history` field in TemporalAIAgent
- [ ] All history mutations go through session API
- [ ] Overlapping RunAsync() calls on same session throw `InvalidOperationException`
- [ ] Distinct sessions run independently without state contamination
- [ ] No regressions in agent execution tests

### Notes

- Agent name validation in session: Defer to separate Phase 2+ work
- Keep changes surgical: Only remove history field, redirect to session
- Concurrent-use rejection is non-negotiable for correctness

---

## Phase 1e: Integration Testing & Benchmarking (Days 5-8)

### Deliverables

1. **Full integration test suite**
   - Session carry-forward across continue-as-new
   - StateBag mutations preserved through LLM + tool cycles
   - History accumulation across turns
   - Legacy session deserialization (no history field)
   - Context-provider state (WorkingSetContextProvider, etc.)

2. **Performance benchmarks**
   - 1-turn workflow: Baseline (< 10ms overhead)
   - 10-turn workflow: Linear growth (< 20ms overhead)
   - 100-turn workflow: Snapshot serialization overhead (< 100ms total)
   - Memory usage: No growth beyond expected history size

3. **Regression test suite**
   - All existing TemporalAIAgent tests still pass
   - All existing TemporalAIAgentProxy tests still pass
   - All existing workflow tests still pass
   - Search attribute registration unchanged

4. **Documentation**
   - Update CLAUDE.md: Session ownership rules
   - Update dos-and-donts.md: Session carry-forward pattern
   - Add migration guide: v0.3 → v0.4 (session ownership)
   - Code comments: StateBag merge semantics

### Success Criteria

- [ ] All integration tests pass (session isolation, history preservation, StateBag flow)
- [ ] Benchmarks pass (overhead < threshold)
- [ ] Zero regressions (all existing tests still pass)
- [ ] Documentation complete and accurate
- [ ] CI/CD pipeline green

### Notes

- Benchmarks validate that serialization overhead is acceptable
- No regression = "ship-ready" check
- Documentation is customer-facing; accuracy is critical

---

## Test Gate Definitions

### Gate 1: Source-Gen Resolver-Origin (Phase 1a)
```csharp
[Fact]
public void SessionSnapshot_SourceGenResolver()
{
    var typeInfo = TemporalAgentJsonUtilities.DefaultOptions
        .GetTypeInfo(typeof(TemporalAgentSessionSnapshot));
    
    Assert.NotNull(typeInfo);
    Assert.Equal(nameof(AgentSessionJsonContext), typeInfo.Origin.Name);
}
```
**Pass Criteria:** TypeInfo origin is `AgentSessionJsonContext` (not reflection)

---

### Gate 2: Polymorphic Payload Round-Trip (Phase 1a)
**Test:** Serialize/deserialize snapshot with request → tool-call → response through TemporalAgentDataConverter

**Components:**
- AgentSessionRequest with ChatMessage(User, "Hello")
- Tool-call message: FunctionCallContent (name, params)
- AgentSessionResponse with FunctionResultContent (result)
- StateBag with test data

**Pass Criteria:** All content types preserved through TemporalAgentDataConverter

---

### Gate 3: Legacy Payload Compatibility (Phase 1a)
**Test:** Deserialize snapshot JSON without `history` field (v0.2 format)

**Pass Criteria:** 
- SessionId parsed correctly
- History deserialized as empty list (not null)
- StateBag restored if present

---

### Gate 4: Empty State Optimization (Phase 1a)
**Test:** Serialize snapshot with null StateBag and null History

**Pass Criteria:**
- JSON has `sessionId` field only
- `stateBag` field absent (not included)
- `history` field absent (not included)

---

### Gate 5: Agent Serialization Boundary (Phase 1b + Phase 1d)
**Test:** Full session round-trip through SerializeSessionAsync/DeserializeSessionAsync

**Components:**
- Create session with StateBag data
- Run agent with history accumulation
- Serialize session
- Deserialize session
- Verify StateBag and history intact

**Pass Criteria:**
- TypeInfo origin is source-gen
- Legacy snapshots (no history) work
- All history entries preserved
- StateBag mutations intact

---

## Concurrent-Use Tests (Phase 1d)

### Test: Same-Session Overlap Rejection
```csharp
[Fact]
public async Task TemporalAIAgent_OverlappingRunAsync_Throws()
{
    var agent = WorkflowAgents.GetTemporalAgent("TestAgent");
    var session = new TemporalAgentSession(...);
    
    var task1 = agent.RunAsync("msg1", session);
    var task2 = agent.RunAsync("msg2", session); // Should throw immediately
    
    await Assert.ThrowsAsync<InvalidOperationException>(() => task2);
}
```
**Pass Criteria:** Second RunAsync throws InvalidOperationException before first completes

---

### Test: Distinct-Session Isolation
```csharp
[Fact]
public async Task TwoSessions_OnSameAgent_IsolateHistory()
{
    var agent = WorkflowAgents.GetTemporalAgent("TestAgent");
    var session1 = new TemporalAgentSession(...);
    var session2 = new TemporalAgentSession(...);
    
    await agent.RunAsync("msg1", session1);
    await agent.RunAsync("msg2", session2);
    
    // session1.History should only contain "msg1" request
    // session2.History should only contain "msg2" request
    Assert.Single(session1.History);
    Assert.Single(session2.History);
}
```
**Pass Criteria:** Sessions have independent history; no contamination

---

## Acceptance Checklist

### Code Quality
- [ ] No compiler warnings
- [ ] No breaking changes to public API (except new snapshot DTO is internal)
- [ ] All code follows project style (see CLAUDE.md)
- [ ] Comments explain StateBag merge semantics

### Testing
- [ ] All 5 gates pass
- [ ] Concurrency tests pass (overlap rejection, isolation)
- [ ] Integration tests pass (carry-forward, history, StateBag)
- [ ] Regression tests pass (no existing test failures)
- [ ] Benchmarks pass (overhead < threshold)

### Performance
- [ ] 1-turn overhead: < 10ms
- [ ] 10-turn overhead: < 20ms
- [ ] 100-turn overhead: < 100ms
- [ ] Memory: No leak (expected growth only)

### Documentation
- [ ] CLAUDE.md updated (session ownership rules)
- [ ] dos-and-donts.md updated (carry-forward pattern)
- [ ] Migration guide written (v0.3 → v0.4)
- [ ] Code comments explain StateBag/history ownership

### Release Readiness
- [ ] CI/CD pipeline green
- [ ] No regressions reported
- [ ] Sample tests updated (if needed)
- [ ] Ready for v0.4.0 tag

---

## Risk Mitigation

### Risk 1: StateBag Ordering Bug
**Mitigation:** Document merge semantics in code; write determinism tests; benchmark order-independence

### Risk 2: Multi-Session Corruption
**Mitigation:** Concurrent-use rejection test; isolation test; manual multi-session workflow

### Risk 3: Continue-as-New Loses State
**Mitigation:** Integration test; validate StateBag and history preserved across CAN

### Risk 4: Performance Regression
**Mitigation:** Benchmarks at 1, 10, 100 turns; validate < threshold before ship

### Risk 5: Reflection Fallback
**Mitigation:** Gate 1 (resolver-origin test) catches if source-gen registration missing

---

## Communication Checkpoints

1. **Phase 1a complete** → Confirm all 4 prototype gates pass
2. **Phase 1b complete** → Confirm Gate 5 (serialization boundary) passes
3. **Phase 1c complete** → Confirm StateBag owned by session; multi-session test passes
4. **Phase 1d complete** → Confirm TemporalAIAgent history removed; concurrency tests pass
5. **Phase 1e complete** → Confirm all integration tests + benchmarks pass; ready for ship

---

## Implementation Notes

- **Use exact code from Phase 0 spec** — Don't innovate; follow design
- **StateBag merge is critical** — Order-dependent serialization; get it right first
- **Concurrent-use rejection is non-negotiable** — Fail-fast prevents data corruption
- **History ownership is the payoff** — Multi-session isolation guaranteed after this phase
- **Benchmarking validates correctness** — Performance overhead must be acceptable

---

## Next Steps

1. Assign Phase 1a (wire-contract prototype) to first developer
2. Parallel: Code review checklist prepared
3. When Phase 1a gates pass: Green light for Phase 1b
4. Each phase completion unlocks next phase
5. After Phase 1e: Ready for v0.4.0 release planning

---

**Spec approved by:** Architecture Team (Morpheus/Tank/Trinity)  
**Implementation ready:** Yes  
**Start date:** 2026-09-08  
**Target completion:** 2026-09-15 (5-8 days)  
**Status:** READY FOR IMPLEMENTATION
