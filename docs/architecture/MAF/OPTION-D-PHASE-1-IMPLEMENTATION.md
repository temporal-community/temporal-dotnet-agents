# Option D: Phase 1 (Revised) — Implementation Specification

**Status:** Ready for Implementation  
**Owner:** Development Team  
**Scope:** Session-owned history + StateBag transfer + wire-contract validation  
**Estimated Duration:** 5-8 engineer-days  
**Start Date:** 2026-09-08  
**Acceptance Criteria:** All test gates passing, benchmark results compared to an established baseline, and no regressions

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

3. **Implement Prototype Test Gates 1-4**
   - Gate 1: Source-gen resolver-origin
   - Gate 2: Polymorphic payload round-trip (with tool-call cycle)
   - Gate 3: Legacy payload compatibility (no history field)
   - Gate 4: Empty state optimization

### Success Criteria

- [ ] TemporalAgentSessionSnapshot compiles and is source-gen registered
- [ ] All 4 prototype gates pass
- [ ] No regressions in existing serialization tests
- [ ] CI/CD builds clean (no warnings)

### Notes

- Do NOT add session-owned history mutations yet (AppendHistoryEntry, SetHistory)
- Do NOT move `_currentStateBag` to session ownership yet
- Snapshot DTO is internal; no public API exposed
- Use exact code from Phase 0 specification
- This phase validates the DTO directly. Do not change the public agent serialization boundary
  until Phase 1b can preserve history as well as the StateBag.
- Do not release Phase 1a independently; the existing direct-session serialization remains until
  the complete snapshot path and its public-boundary test land in Phase 1b.

---

## Phase 1b: Session-Owned History Foundation (Days 2-3)

### Deliverables

1. **Add history storage to TemporalAgentSession**
   ```csharp
   private readonly List<DurableSessionEntry> _history = [];
   internal IReadOnlyList<DurableSessionEntry> History => _history;
   internal void AppendHistoryEntry(DurableSessionEntry entry) { ... }
   internal void RestoreHistory(IEnumerable<DurableSessionEntry> entries) { ... }
   ```

2. **Implement the public serialization boundary**
   - Serialization: Include history in snapshot
   - Deserialization: Restore `snapshot.History ?? []` through `RestoreHistory`
   - `SerializeSessionCoreAsync()` and `DeserializeSessionCoreAsync()` use the generated
     snapshot metadata, never direct session serialization

3. **Implement Gate 5: Agent Serialization Boundary**
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
- Keep `AppendHistoryEntry` O(1): append to the private `List<T>` and expose only its
  `IReadOnlyList<T>` facade. Do not rebuild a list on each append.

---

## Phase 1c: StateBag Transfer to Session (Days 3-4)

### Deliverables

1. **Make the inherited `TemporalAgentSession.StateBag` the only authoritative StateBag**
   - Do not add a second `_currentStateBag` field to the session.
   - Reuse `SerializeStateBag()` for activity dispatch and add an internal operation that replaces
     the current `StateBag` from a merged `JsonElement?` value.
   - The replacement operation assigns the deserialized bag to the inherited protected
     `StateBag` setter; it treats null, undefined, and JSON null as an empty bag.
   - Remove `_currentStateBag` from `TemporalAIAgent`.

2. **Apply the defined StateBag operations in the agent loop**
   - Before each activity dispatch, use the session's serialized StateBag.
   - After an LLM step, call the session operation that overlays trusted output using
     `StateBagMerge.OverlayTrustedStateBag`.
   - After the complete tool/interceptor fan-out, call the session operation that merges
     write-backs using `StateBagMerge.Merge` in original tool-call index order.

3. **Implement StateBag merge rules** (determinism-critical)
   - After LLM step: Overlay trusted LLM output
   - After tool fan-out: Merge tool/interceptor write-backs in tool-call index order
   - Document merge semantics in code comments

4. **Update TemporalAIAgent.RunCoreAsync**
   - Remove local `_currentStateBag` mutations.
   - Do not replace an LLM-step result directly: the current direct assignment must become the
     trusted overlay operation.
   - Preserve the existing security filtering of tool/interceptor write-backs.

### Success Criteria

- [ ] StateBag owned by session (not agent)
- [ ] Multi-session test: Two sessions on same agent have isolated StateBag
- [ ] Continue-as-new: StateBag mutations preserved across CAN boundary
- [ ] StateBag merge is deterministic despite activity scheduling: tool/interceptor contributions
  are merged in original tool-call index order and later indexes win on conflicts
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

3. **Add session-local concurrent-use rejection**
   ```csharp
   // TemporalAgentSession
   private bool _runInProgress;

   internal void EnterRun()
   {
       if (_runInProgress)
           throw new InvalidOperationException(
               "Overlapping RunAsync() calls on the same session are not allowed. " +
               "Use distinct session objects for parallel conversations.");
       _runInProgress = true;
   }

   internal void ExitRun() => _runInProgress = false;
   ```
   - Call `EnterRun()` after the session is validated and before the first await in `RunCoreAsync`.
   - Call `ExitRun()` in a `finally` block covering every completion, cancellation, and failure path.
   - Do not put this flag on `TemporalAIAgent`; one agent must support distinct live sessions.

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
   - Establish a baseline on the pre-change implementation using representative message, tool-call,
     and StateBag payload sizes.
   - Measure 1-, 10-, and 100-turn workflows with the same fixture after the change.
   - Record median and p95 elapsed time, allocation, and serialized snapshot size.
   - Set the pass/fail regression budget from those baseline measurements; do not use unvalidated
     absolute millisecond thresholds.

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
- [ ] Benchmarks meet the documented regression budget relative to the established baseline
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
    Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
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
**Test:** Deserialize snapshot JSON without a `history` field (the supported legacy wire shape)

**Pass Criteria:** 
- SessionId parsed correctly
- DTO `History` is null; the later public agent deserializer turns it into an empty session history
- StateBag restored if present

---

### Gate 4: Empty State Optimization (Phase 1a)
**Test:** Serialize snapshot with null StateBag and null History

**Pass Criteria:**
- JSON has `sessionId` field only
- `stateBag` field absent (not included)
- `history` field absent (not included)

---

### Gate 5: Agent Serialization Boundary (Phase 1b–1d)
**Test:** Full session round-trip through SerializeSessionAsync/DeserializeSessionAsync

**Components:**
- **Phase 1b structural subcase:** create a session, add typed StateBag and history entries
  directly, then serialize and deserialize it through the public MAF boundary.
- **Phase 1d end-to-end subcase:** run the agent to accumulate history and StateBag mutations,
  then serialize and deserialize it.
- In both subcases, restore a legacy snapshot without `history` through
  `DeserializeSessionAsync`.

**Pass Criteria:**
- TypeInfo origin is source-gen
- Legacy snapshots (no history) work
- All history entries preserved
- StateBag mutations intact

---

## Concurrent-Use Tests (Phase 1d)

### Test: Same-Session Overlap Rejection
**Fixture requirements:**
- Execute inside a Temporal workflow integration fixture; `TemporalAIAgent` cannot run from an
  ordinary unit-test thread.
- Use a controllable first activity to suspend the first run after `session.EnterRun()` succeeds.
- Start the second run with that same live session and assert it faults with
  `InvalidOperationException` before another activity is scheduled.
- Release the first activity and assert its original run completes, proving `ExitRun()` ran in the
  completion path.

**Pass Criteria:** Second RunAsync throws InvalidOperationException before first completes

---

### Test: Distinct-Session Isolation
**Fixture requirements:**
- Execute both completed runs in a Temporal workflow integration fixture using one agent and two
  distinct live sessions.
- Assert each session contains only its own request and response entries (two entries after one
  completed successful run).
- Assert neither accumulated-message input nor StateBag contains data belonging to the other
  session.

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
- [ ] Benchmarks meet the documented regression budget relative to the established baseline

### Performance
- [ ] 1-, 10-, and 100-turn results recorded against the pre-change baseline
- [ ] Median/p95 time, allocation, and serialized snapshot size meet the documented regression budget
- [ ] Memory growth is attributable to retained history and bounded by the fixture's expected history size

### Documentation
- [ ] CLAUDE.md updated (session ownership rules)
- [ ] dos-and-donts.md updated (carry-forward pattern)
- [ ] Migration guide written (v0.3 → v0.4)
- [ ] Code comments explain StateBag/history ownership

### Release Readiness
- [ ] CI/CD pipeline green
- [ ] No regressions reported
- [ ] Sample tests updated (if needed)
- [ ] Eligible for a separate release-readiness review

---

## Risk Mitigation

### Risk 1: StateBag Ordering Bug
**Mitigation:** Document merge semantics in code; write scheduling-independent determinism tests;
benchmark the fixed index-order merge

### Risk 2: Multi-Session Corruption
**Mitigation:** Concurrent-use rejection test; isolation test; manual multi-session workflow

### Risk 3: Continue-as-New Loses State
**Mitigation:** Integration test; validate StateBag and history preserved across CAN

### Risk 4: Performance Regression
**Mitigation:** Benchmark at 1, 10, and 100 turns against the established baseline; enforce the
documented regression budget before ship

### Risk 5: Reflection Fallback
**Mitigation:** Gate 1 (resolver-origin test) catches if source-gen registration missing

---

## Communication Checkpoints

1. **Phase 1a complete** → Confirm all 4 prototype gates pass
2. **Phase 1b complete** → Confirm Gate 5's structural serialization subcase passes
3. **Phase 1c complete** → Confirm StateBag owned by session; multi-session test passes
4. **Phase 1d complete** → Confirm TemporalAIAgent history removed, Gate 5's end-to-end subcase,
   and concurrency tests pass
5. **Phase 1e complete** → Confirm all integration tests + benchmarks pass; ready for ship

---

## Implementation Notes

- **Use exact code from Phase 0 spec** — Don't innovate; follow design
- **StateBag merge is critical** — fixed tool-call index order makes it deterministic despite
  non-deterministic activity completion; preserve that rule
- **Concurrent-use rejection is non-negotiable** — Fail-fast prevents data corruption
- **History ownership is the payoff** — Multi-session isolation guaranteed after this phase
- **Benchmarking validates performance** — Performance overhead must remain within the documented
  regression budget

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
