# Architectural Recommendation: Durable Shared History for TemporalAIAgent

**Status**: Architectural Review  
**Date**: 2026-09-08  
**Scope**: Making `TemporalAIAgent` conversation history durable and shared across multiple sessions/instances  

---

## Executive Summary

**The Problem**: `TemporalAIAgent` (sub-agent) currently stores conversation history in an in-memory `List<DurableSessionEntry> _history` that:
- ❌ Is lost when the orchestrating workflow crashes and replays
- ❌ Cannot persist across multiple sessions using the same agent instance
- ❌ Does not guarantee durability on worker failure

**The Requirement** (as clarified):
- ✅ Multiple sessions from same agent instance SHOULD share history (intentional learning)
- ✅ History must be durable and survive workflow replay
- ✅ Must be efficient for long-running agents (handle large histories)
- ✅ Must not degrade LLM-call latency significantly
- ✅ Replay-safe and deterministic

---

## Current Architecture: How AgentWorkflow Handles History

To understand the best approach for `TemporalAIAgent`, we must first understand how the *orchestrating* workflow handles history:

### AgentWorkflow's History Model

1. **Workflow-Local History Storage**:
   ```csharp
   // In DurableChatWorkflowBase<TOutput> (base class)
   private List<DurableSessionEntry> _history = new(16);
   ```
   - Stores both request and response entries for every turn
   - Replayed from Temporal event history on each replay

2. **Continue-as-New (CAN) Carry-Forward**:
   ```csharp
   // When history grows beyond MaxEntryCount (default 1000):
   var carriedInput = _input with
   {
       CarriedHistory = input.CarriedHistory,  // Trimmed/reduced history
       // ... other fields ...
   };
   return Workflow.CreateContinueAsNewException(
       (AgentWorkflow wf) => wf.RunAsync(carriedInput));
   ```
   - Slices history to keep N most recent entries
   - Optionally runs a `HistoryReducer` activity for compression
   - Passes compressed history to new workflow run
   - New workflow restores: `_history.AddRange(input.CarriedHistory)`

3. **Restore-on-Replay**:
   - History entries are deterministically reconstructed from Temporal event history
   - On continue-as-new, carried history seed is added first
   - Each new event replays and appends to `_history`

### Why AgentWorkflow Can Do This: Continuation Control

`AgentWorkflow` is a **long-lived workflow** — it controls its own lifetime and can decide *when* to do continue-as-new. It owns:
- The decision to CAN (triggered by history size)
- The input payload to pass forward
- The opportunity to prune/compress history before the transition

---

## TemporalAIAgent's Constraints: Why It Can't Follow AgentWorkflow's Pattern

### Constraint 1: TemporalAIAgent Lives Inside a Workflow

```csharp
// Inside AgentWorkflow.ExecuteDurableAgentTurnAsync:
var tempAgentProxy = WorkflowAgents.GetTemporalAgent("SubAgentName");
var response = await tempAgentProxy.RunAsync(messages, session, options);
```

- `TemporalAIAgent` is instantiated inside workflow code
- It executes as a sub-agent of the orchestrating workflow
- It has no control over workflow lifecycle (no continue-as-new, no input seeding)

### Constraint 2: Each Invocation Recreates the Instance

```csharp
// WorkflowAgents.GetTemporalAgent creates a NEW instance each call:
internal TemporalAIAgent(string agentName, ActivityOptions? activityOptions = null)
{
    _agentName = agentName;
    _history = [];  // ← Fresh list every time
    // ...
}
```

- The in-memory `_history` is instance-local
- After the method returns, the instance is eligible for GC
- Next invocation gets a fresh `_history`

### Constraint 3: No Direct Workflow-Input Access

`TemporalAIAgent` does not receive the orchestrating workflow's input. It cannot:
- Read `CarriedHistory` from the parent workflow's input
- Mutate the parent workflow's input to carry history forward
- Coordinate with the parent's continue-as-new decision

---

## Design Options

### Option A: Store History in StateBag (Per-Turn Carry-Forward)

**Concept**: Use the existing `_currentStateBag` pattern from `TemporalAIAgent` to carry sub-agent history forward across steps within a turn, then across turns via `AgentWorkflow`'s StateBag carry-forward.

**Mechanism**:
1. At start of turn: Populate `TemporalAIAgent._history` from `AgentStepInput.SerializedStateBag`
2. At each step: Update `_history` with new entries
3. At turn end: Write `_history` to the activity's `UpdatedStateBag`
4. `AgentWorkflow` merges and carries forward via its own StateBag mechanism

**Pros**:
- Leverages existing `_currentStateBag` infrastructure
- Minimal new code paths
- Works within single agent instance per turn
- No new serialization contracts

**Cons**:
- History for long-running agents grows inside StateBag (64 KB size guard already exists)
- History must be re-serialized on every activity dispatch
- Requires changes to `AgentStepInput` to include history blob
- Sub-agent history mixed with context-provider state (unclear ownership)
- History loss if a step's activity crashes mid-execution (not persisted until return)

**Complexity**: Medium  
**Performance Impact**: Moderate (re-serialization per activity)  
**Estimated Effort**: 2–3 weeks

---

### Option B: Separate Durable History Store (External Activity/Table)

**Concept**: Move sub-agent history out of `TemporalAIAgent` into a durable external store (database, cache, or a background Temporal workflow).

**Mechanism**:
1. `TemporalAIAgent` dispatches a `SaveSubAgentHistory` activity at turn end
2. Activity stores `{AgentName, SessionId?, HistoryEntries}` in a durable store
3. At sub-agent instantiation: Activity queries `LoadSubAgentHistory(AgentName, SessionId?)`
4. `TemporalAIAgent` restores history from the query result

**Pros**:
- True durability independent of workflow state
- History not constrained by StateBag size (64 KB)
- Supports cross-session history queries
- Can implement TTL/cleanup policies per agent

**Cons**:
- Network round-trip per sub-agent turn (latency)
- Requires external infrastructure (database, storage service)
- Adds operational complexity
- State lives outside Temporal (requires separate backup/disaster recovery)
- Coupling between agent name and history (session identity unclear)

**Complexity**: High  
**Performance Impact**: High (added network latency per turn)  
**Estimated Effort**: 4–6 weeks (includes store integration)

---

### Option C: Carried Through Orchestrating Workflow (Recommended)

**Concept**: Make the orchestrating `AgentWorkflow` own and carry forward all sub-agent histories through its own continue-as-new mechanism.

**Mechanism**:

1. **Add per-agent history map to AgentWorkflow**:
   ```csharp
   private Dictionary<string, List<DurableSessionEntry>> _subAgentHistories = new();
   ```

2. **At turn start**: Seed TemporalAIAgent with carried history:
   ```csharp
   var subAgentName = "MySubAgent";
   var subAgentHistory = _subAgentHistories.TryGetValue(subAgentName, out var h) ? h : [];
   var tempAgent = WorkflowAgents.GetTemporalAgent(subAgentName);
   
   // Pass history via a new agent-run option or internal mechanism
   var response = await tempAgent.RunAsync(
       messages, 
       session, 
       options: new TemporalAgentRunOptions 
       { 
           CarriedHistory = subAgentHistory
       });
   ```

3. **At turn end**: Capture updated history from the activity result:
   ```csharp
   // Activity returns history delta or full updated history
   var updatedHistory = activityResult.UpdatedSubAgentHistory;
   _subAgentHistories[subAgentName] = updatedHistory;
   ```

4. **Carry forward in CAN**:
   ```csharp
   protected override ContinueAsNewException CreateContinueAsNewException(DurableChatWorkflowInput input)
   {
       var carriedInput = _input with
       {
           CarriedSubAgentHistories = _subAgentHistories.ToDictionary(
               kvp => kvp.Key, 
               kvp => TrimSubAgentHistory(kvp.Value, maxPerAgent: 100)),
           // ... other fields ...
       };
       return Workflow.CreateContinueAsNewException(
           (AgentWorkflow wf) => wf.RunAsync(carriedInput));
   }
   ```

5. **Restore on new run**:
   ```csharp
   [WorkflowRun]
   public async Task RunAsync(AgentWorkflowInput input)
   {
       _input = input;
       _subAgentHistories = input.CarriedSubAgentHistories ?? new();
       await base.RunAsync(input);
   }
   ```

**Pros**:
- ✅ Leverages existing AgentWorkflow continue-as-new infrastructure
- ✅ Minimal surface area change (extend `AgentWorkflowInput`)
- ✅ History is deterministically replayed from workflow history
- ✅ No external dependencies or network round-trips
- ✅ Single authority for all history (orchestrating workflow)
- ✅ Supports per-agent history trimming/reduction
- ✅ Works with multiple sub-agents (map by name)
- ✅ Deterministic and replay-safe

**Cons**:
- ❌ Sub-agent history is now part of orchestrating workflow input size
- ⚠️ If sub-agent history grows unboundedly, it pushes workflow input size (should apply per-agent trim cap)
- ⚠️ Requires new optional field on `AgentWorkflowInput` (backwards compat: use `??` default)
- ⚠️ Needs new property on `TemporalAgentRunOptions` to inject carried history
- ⚠️ Activity layer needs to return history delta or full updated history

**Complexity**: Low–Medium  
**Performance Impact**: None (same dispatch pattern, added JSON serialization for history blob)  
**Estimated Effort**: 2–3 weeks  
**Breaking Changes**: Additive (optional fields)

---

## Recommendation: **Option C (Carried Through Orchestrating Workflow)**

### Rationale

1. **Alignment with existing patterns**: The orchestrating `AgentWorkflow` already carries state forward through continue-as-new. Extending this to sub-agent histories is a natural and proven pattern.

2. **Determinism and durability**: History is reconstructed from Temporal's event history, not external storage. This is inherently deterministic and survives any worker or storage failure.

3. **No new infrastructure**: Does not require a database, cache, or external service. Keeps the solution contained within Temporal.

4. **Minimal latency impact**: No extra network round-trips. History is available on the workflow thread.

5. **Backwards compatible**: New optional fields on `AgentWorkflowInput` default to empty; existing workflows continue to work.

6. **Supports multi-agent orchestration**: Map of sub-agent names to histories handles complex workflows with multiple sub-agents elegantly.

7. **Explicit and testable**: The orchestrating workflow is the authority. Tests can mock history injection without external services.

---

## Implementation Roadmap

### Phase 1: Data Model (1 week)

1. **Extend `AgentWorkflowInput`**:
   ```csharp
   public class AgentWorkflowInput : DurableChatWorkflowInput
   {
       // New optional field
       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
       public Dictionary<string, List<DurableSessionEntry>>? CarriedSubAgentHistories 
       { get; init; }
   }
   ```

2. **Extend `TemporalAgentRunOptions`** (or create a parallel options type):
   ```csharp
   public class TemporalAgentRunOptions : AgentRunOptions
   {
       public List<DurableSessionEntry>? CarriedHistory { get; set; }
   }
   ```

3. **Update `AgentStepInput`**:
   ```csharp
   public class AgentStepInput
   {
       // Existing fields...
       
       // New: sub-agent should include this in accumulated messages if provided
       [JsonPropertyName("carriedHistory")]
       public List<DurableSessionEntry>? CarriedHistory { get; set; }
   }
   ```

### Phase 2: Workflow-Level Carry-Forward (1 week)

1. **Modify `AgentWorkflow.RunAsync`**:
   ```csharp
   [WorkflowRun]
   public async Task RunAsync(AgentWorkflowInput input)
   {
       _input = input;
       _subAgentHistories = new(input.CarriedSubAgentHistories ?? new());
       // ... existing code ...
   }
   ```

2. **Update `CreateContinueAsNewException`**:
   ```csharp
   protected override ContinueAsNewException CreateContinueAsNewException(DurableChatWorkflowInput input)
   {
       var trimmedSubAgentHistories = new Dictionary<string, List<DurableSessionEntry>>();
       foreach (var kvp in _subAgentHistories)
       {
           trimmedSubAgentHistories[kvp.Key] = DefaultBoundedTrim(
               kvp.Value, 
               maxPerAgent: 100);  // Config: cap per agent to keep CAN payload bounded
       }
       
       var carriedInput = _input with
       {
           CarriedSubAgentHistories = trimmedSubAgentHistories,
           // ... existing fields ...
       };
       
       return Workflow.CreateContinueAsNewException(
           (AgentWorkflow wf) => wf.RunAsync(carriedInput));
   }
   ```

3. **Add logging/diagnostics**:
   - Log total sub-agent histories size at CAN time
   - Warn if any single agent history exceeds N entries
   - Track per-agent history growth

### Phase 3: Sub-Agent Integration (1 week)

1. **Modify `TemporalAIAgent.RunCoreAsync`**:
   ```csharp
   protected override async Task<AgentResponse> RunCoreAsync(
       IEnumerable<ChatMessage> messages,
       AgentSession? session = null,
       AgentRunOptions? options = null,
       CancellationToken cancellationToken = default)
   {
       // Seed history from carried option
       if (options is TemporalAgentRunOptions { CarriedHistory: { Count: > 0 } carried })
       {
           foreach (var entry in carried)
           {
               _history.Add(entry);
           }
       }
       
       // ... existing dispatch loop ...
   }
   ```

2. **Update `AgentActivities.RunDurableAgentStepAsync`**:
   - Accept carried history in `AgentStepInput`
   - Forward to `TemporalAIAgent` via a new options field or direct pass
   - Return updated history entries in `AgentStepResult`

3. **In orchestrating workflow**: Intercept activity result and update `_subAgentHistories`:
   ```csharp
   var stepResult = await Workflow.ExecuteActivityAsync(...);
   
   if (stepResult.UpdatedSubAgentHistory is { Count: > 0 })
   {
       _subAgentHistories[_agentName] = stepResult.UpdatedSubAgentHistory;
   }
   ```

### Phase 4: Testing & Documentation (1 week)

1. **Unit tests**:
   - Sub-agent history is seeded from carried history
   - History entries accumulate across turns
   - History is trimmed before CAN
   - Empty history handled gracefully

2. **Integration tests**:
   - Sub-agent history survives workflow crash/replay
   - Multiple sub-agents have separate histories
   - Continue-as-new preserves sub-agent history (bounded)

3. **Documentation**:
   - Update `docs/how-to/MAF/usage.md` with sub-agent history patterns
   - Architecture section: explain carry-forward mechanism
   - Sample: multi-agent workflow with persistent sub-agent history

---

## Trade-offs and Alternatives Revisited

| Aspect | Option A (StateBag) | Option B (External Store) | **Option C (Orchestrating WF)** |
|--------|---|---|---|
| **Durability** | ✅ Via AgentWorkflow | ✅ True external | ✅ Via Temporal event history |
| **Determinism** | ⚠️ Needs care in merge | ⚠️ External service drift | ✅ Deterministic replay |
| **Latency** | ✅ No extra RTT | ❌ +1 RTT per turn | ✅ No extra RTT |
| **Scalability** | ⚠️ StateBag size limits | ✅ Unbounded | ⚠️ CAN payload size limits |
| **Operational Complexity** | ✅ Minimal | ❌ External infra | ✅ Minimal |
| **Code Complexity** | ⚠️ Merge logic | ❌ Store integration | ✅ Straightforward |
| **Backwards Compat** | ✅ Optional | ✅ Optional | ✅ Optional |
| **Estimated Effort** | 2–3 weeks | 4–6 weeks | **2–3 weeks** |

---

## Potential Issues and Mitigations

### Issue 1: Sub-Agent History Grows Unboundedly

**Problem**: If a sub-agent is used frequently over days/weeks, its history could grow to megabytes.

**Mitigation**:
- Cap per-agent history at N entries (default: 100, configurable)
- Apply optional per-agent `HistoryReducer` (similar to main agent)
- Log when a sub-agent history exceeds cap; trim oldest entries
- Allow per-agent TTL on entries (e.g., keep only last 7 days)

### Issue 2: CAN Payload Size Bloat

**Problem**: Carrying large sub-agent histories through continue-as-new could exceed Temporal's workflow input size limits.

**Mitigation**:
- Measure total carried history size; log warning if >1 MB
- Trim aggressively (default: 50 entries per agent, not 100)
- Consider optional external history store as fallback for high-volume agents
- Test CAN with realistic history sizes in integration suite

### Issue 3: Multiple Sub-Agent Instances with Same Name

**Problem**: If the orchestrating workflow creates two separate `TemporalAIAgent` instances for the same agent name (e.g., for parallel dispatch), they share the same carried history from the map.

**Mitigation**:
- Document: sub-agents with the same name intentionally share history (feature, not bug)
- If separate histories needed, use distinct agent names
- Consider optional session ID as secondary map key: `_subAgentHistories[AgentName][SessionId]`

### Issue 4: Backwards Compatibility During Rollout

**Problem**: Old workflow instances in-flight might not have the new `CarriedSubAgentHistories` field.

**Mitigation**:
- New field is optional (default to `null`, which means empty map)
- Old workflows continue to work; new sub-agent history is not carried on their next CAN
- Once a workflow does a CAN after code deployment, new field is populated and forward-compatible

---

## Summary

**Recommendation: Implement Option C** — carry sub-agent history through the orchestrating `AgentWorkflow`'s continue-as-new mechanism.

**Key properties**:
- ✅ Durable (replayed from Temporal event history)
- ✅ Shared across sessions (same agent instance uses same map)
- ✅ Deterministic and replay-safe
- ✅ No external dependencies
- ✅ Minimal latency impact
- ✅ Backwards compatible
- ✅ Supports multi-agent orchestration

**Effort estimate**: 2–3 weeks  
**Risk**: Low (leverages proven continue-as-new pattern)  
**Breaking changes**: None (additive optional fields)

This approach aligns TemporalAIAgent's durability model with the existing AgentWorkflow infrastructure, providing a clean, maintainable solution that scales to complex multi-agent workflows.
