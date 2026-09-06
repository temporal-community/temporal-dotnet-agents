# Durability and Determinism in Temporal Agent Workflows

This document explains how Temporal's durability and determinism guarantees work in the context of agent orchestration workflows, particularly when workers crash or are interrupted.

## Quick Answer

**When an orchestrating workflow with agent calls crashes and restarts:**
- ✅ Completed `agent.RunAsync()` calls are **durable** — they are **NOT re-executed**
- ✅ A new worker will **deterministically replay** from history and **return cached results**
- ✅ The workflow continues from where it left off (or from the last safe checkpoint)

---

## Temporal's Determinism Guarantee

Temporal workflows are designed to be **deterministic** — they must produce the same result every time they're replayed. This is achieved through:

1. **Event Sourcing**: Every workflow action (activity execution, decision, etc.) is recorded in an immutable event history
2. **Deterministic Replay**: When a workflow resumes after a crash, it replays from the beginning, but returns cached results from history instead of re-executing side effects
3. **Activity Idempotence**: Activities are executed at most once per call from the workflow's perspective (though they may be retried internally)

---

## Agent Call Durability: Step-by-Step

### Initial Execution

```csharp
[WorkflowRun]
public async Task<string> RunAsync(string userQuestion)
{
    var agent = GetTemporalAgent("WeatherAssistant");
    var session = await agent.CreateSessionAsync();

    // First agent call
    var response1 = await agent.RunAsync("Question 1", session);  // ← ExecuteActivityAsync(Activity1)
    Console.WriteLine($"Response 1: {response1.Text}");

    // Second agent call
    var response2 = await agent.RunAsync("Question 2", session);  // ← ExecuteActivityAsync(Activity2)
    Console.WriteLine($"Response 2: {response2.Text}");

    // Regular activity call
    var otherResult = await Workflow.ExecuteActivityAsync(
        (OtherActivities a) => a.DoSomething());                  // ← ExecuteActivityAsync(Activity3)

    return "Complete";
}
```

**Event History After Initial Execution:**

```
Event 1: WorkflowExecutionStarted
Event 2: ActivityScheduled (Activity1)
Event 3: ActivityCompleted (Activity1) ← Result: AgentResponse(...) [CACHED]
Event 4: ActivityScheduled (Activity2)
Event 5: ActivityCompleted (Activity2) ← Result: AgentResponse(...) [CACHED]
Event 6: ActivityScheduled (Activity3)
Event 7: ActivityCompleted (Activity3) ← Result: ... [CACHED]
Event 8: WorkflowExecutionCompleted
```

### Worker Crashes Mid-Execution

Suppose the worker crashes right after Activity2 completes:

```
[Worker Execution]
  Activity1 ✓ (completes)
  Activity2 ✓ (completes)
  [CRASH - other business logic fails]
  Activity3 ✗ (never started)
```

The event history at crash time:

```
Event 1: WorkflowExecutionStarted
Event 2: ActivityScheduled (Activity1)
Event 3: ActivityCompleted (Activity1) ← Result cached
Event 4: ActivityScheduled (Activity2)
Event 5: ActivityCompleted (Activity2) ← Result cached
[Missing] Event 6: ActivityScheduled (Activity3)
```

### New Worker Resumes

A new worker picks up the workflow and replays it:

```
[Replay Execution - New Worker]
  Step: Await agent.RunAsync("Question 1", session)
    → Check history for Event 3
    → Find ActivityCompleted result
    → Return cached AgentResponse (DO NOT re-execute Activity1) ✓

  Step: Await agent.RunAsync("Question 2", session)
    → Check history for Event 5
    → Find ActivityCompleted result
    → Return cached AgentResponse (DO NOT re-execute Activity2) ✓

  Step: Await OtherActivities.DoSomething()
    → Check history for Event 6
    → NOT FOUND - this is a new activity execution
    → Schedule Activity3 for execution ✓
    → Activity3 runs and completes
    → Result is recorded in history
```

**New Event History:**

```
Event 1: WorkflowExecutionStarted
Event 2: ActivityScheduled (Activity1)
Event 3: ActivityCompleted (Activity1) ← CACHED - not re-executed
Event 4: ActivityScheduled (Activity2)
Event 5: ActivityCompleted (Activity2) ← CACHED - not re-executed
Event 6: ActivityScheduled (Activity3)  ← NEW - was missing before
Event 7: ActivityCompleted (Activity3)  ← NEW - now completes
Event 8: WorkflowExecutionCompleted
```

---

## Under the Hood: How Agent Calls Become Activities

There are two distinct paths through which agent work gets dispatched, depending on whether the call originates from outside a workflow or from inside one.

### Path A — External Caller → AgentWorkflow (via `DefaultTemporalAgentClient`)

An external caller (API server, console app, `TemporalAIAgentProxy`) goes through `DefaultTemporalAgentClient`, which owns the Temporal client and starts/reuses the session workflow directly:

```
External Caller (e.g. TemporalAIAgentProxy)
    ↓
    ITemporalAgentClient.SendAsync(sessionId, request)
    ↓
    DefaultTemporalAgentClient.SendAsync()
    ↓
    client.StartWorkflowAsync(AgentWorkflow, IdConflictPolicy = UseExisting)
        ← Creates or no-ops; establishes the durable session workflow
    ↓
    handle.ExecuteUpdateAsync(wf => wf.RunAgentAsync(request))
        ← Blocks until the update handler returns
    ↓
    AgentWorkflow [WorkflowUpdate("Run")]
        ← Serializes via _isProcessing, records request in _history
    ↓
    Loop: Workflow.ExecuteActivityAsync(AgentActivities.RunDurableAgentStepAsync)
        ← One LLM call per dispatch
        ← For each pending tool call, fan out via Workflow.WhenAllAsync over
          AgentActivities.InvokeAgentToolAsync (one activity per tool)
    ↓
    AgentActivities.RunDurableAgentStepAsync [Activity]
        ← Resolves CachedDurableAgent (lazy compose), calls IChatClient
    ↓
    Final AgentResponse returned to AgentWorkflow → recorded in _history
    ↓
    Update response returned to DefaultTemporalAgentClient → returned to caller
```

### Path B — Orchestrating Workflow → Sub-Agent (via `TemporalAIAgent`)

Inside an orchestrating `[Workflow]`, `GetTemporalAgent()` returns a `TemporalAIAgent` that dispatches inference by calling `Workflow.ExecuteActivityAsync` directly — without starting a separate session workflow:

```
Orchestrating [Workflow] (e.g. ResearchWorkflow)
    ↓
    var agent = GetTemporalAgent("ResearcherAgent");
    var session = await agent.CreateSessionAsync();
    await agent.RunAsync(messages, session)
    ↓
    TemporalAIAgent.RunCoreAsync()
        ← Appends request to TemporalAIAgent._history (workflow state)
    ↓
    Drive the durable loop in-place:
      Workflow.ExecuteActivityAsync(
        (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
        activityOptions)
        ← Activity result is recorded in the orchestrating workflow's event history
      Per pending tool call:
        Workflow.ExecuteActivityAsync(
          (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
          toolOptions)
    ↓
    Final AgentResponse returned to TemporalAIAgent → appended to _history as response entry
    ↓
    AgentResponse returned to orchestrating workflow code
```

### Why This Ensures Durability

1. **Activity Results are History**: The `AgentResponse` is recorded as an activity completion event in whichever workflow scheduled it
2. **History is Immutable**: Once recorded, the event cannot be changed
3. **Replay is Deterministic**: Future replays of the workflow retrieve the cached result without re-executing
4. **Agent Workflow is Separate (Path A only)**: When called externally, `AgentWorkflow` maintains its own independent history and state; the orchestrating workflow records only the activity result

---

## Important Nuances

### 1. Activity Retries vs. Workflow Replays

| Concept | Scope | Retry Behavior |
|---------|-------|---|
| **Activity Retry** | Within a single activity execution | Temporal retries internally (configurable via `ActivityOptions`) |
| **Workflow Replay** | Across multiple workflow executions | Results from history, never re-executed from scratch |

### 2. Agent Workflow Durability is Separate

The `AgentWorkflow` (the sibling workflow started by the activity) has **independent durability**:

```
Orchestrating Workflow [History A]
    ↓
    Activity calls StartWorkflowAsync(AgentWorkflow)
    ↓
    Agent Workflow [History B] ← Independent history

If either crashes:
  - Orchestrating Workflow: Replayed from History A
  - Agent Workflow: Resumed from History B
```

### 3. Continue-as-New and History Carryover

`AgentWorkflow` extends the shared `DurableChatWorkflowBase<AgentResponse>` (from
`TemporalCommunity.Extensions.AI`), which owns the wait/trigger loop for continue-as-new — it is
not inline in `AgentWorkflow`. The base class waits on SDK-suggested CAN, `MaxEntryCount`, or an
explicit shutdown, then invokes the `CreateContinueAsNewException` hook that `AgentWorkflow`
overrides to build the MAF-specific carried input (StateBag snapshot, approval ledger, and the
base-class CAN fields). No data is lost: the old run completes, a new run starts with the carried
history, and the orchestrating workflow's unpinned handle automatically follows the chain. For the
exact override code and the full field list carried forward, see
[Agent Sessions and the Workflow Loop — Continue-as-New: History Carryover](./agent-sessions-and-workflow-loop.md#continue-as-new-history-carryover).

---

## Failure Scenarios and Outcomes

The general pattern — activities completed before a crash return cached results on replay, and
in-flight activities retry on the new worker — is illustrated above. For the concrete crash
scenarios specific to this library (worker dies mid-activity, mid-workflow-code, or mid-Update;
what happens to the StateBag and turn count in each case; exact heartbeat and timeout mechanics),
see [Agent Sessions and the Workflow Loop — Crashes, Heartbeats, and Timeouts](./agent-sessions-and-workflow-loop.md#crashes-heartbeats-and-timeouts),
which covers these scenarios against the actual `AgentWorkflow` implementation rather than a
generic `Activity1`/`Activity2`/`Activity3` example.

---

## Best Practices

### ✅ DO

- **Trust Temporal's replay mechanism** — activities in history will not be re-executed
- **Design activities to be idempotent** — assume they might be retried internally
- **Use `ActivityOptions` wisely** — set appropriate timeouts for agent calls
- **Monitor workflow history** — use Temporal CLI/UI to inspect event history after crashes
- **Test failure scenarios** — simulate worker crashes to verify recovery

### ❌ DON'T

- **Assume activities run multiple times** — they don't (from the workflow's perspective)
- **Modify activity logic assuming determinism** — use deterministic decision points (`Workflow.Random`, `Workflow.GetVersion`)
- **Expect real-time consistency** — Temporal is eventually consistent, not strongly consistent
- **Rely on wall-clock time** — use `Workflow.UtcNow` instead of `DateTime.UtcNow`
- **Forget about long-running workflows** — set appropriate TTLs and use continue-as-new

---

## Verification: Checking Durability in Practice

### Using Temporal CLI

```bash
# View workflow history
temporal workflow show --workflow-id <workflow-id>

# Check event history
temporal workflow show --workflow-id <workflow-id> --output json | jq '.history.events'

# Look for ActivityCompleted events:
# They will show the cached result on replay
```

### In Code: Testing Durability

```csharp
// After workflow completes, crash the worker and restart
// The workflow will replay and should reach the same completion state

// Verify by:
// 1. Querying the workflow history
// 2. Checking that Activity events are the same as before
// 3. Verifying no duplicate activity executions occurred
```

---

## Summary Table

| Question | Answer |
|----------|--------|
| Are completed agent calls durable? | ✅ Yes - recorded in event history |
| Will completed activities re-run after worker crash? | ❌ No - cached results are returned |
| Is the Agent Workflow separately durable? | ✅ Yes - it has independent history |
| Can a workflow resume after partial completion? | ✅ Yes - from the last checkpoint |
| Will conversation history be lost on crash? | ❌ No - stored in workflow state and carried forward |
| Should activities be idempotent? | ✅ Yes - in case of internal retries |
| What if an activity fails and is retried? | Activity retry logic is independent; workflow waits for result |

---

## References

- [Agent Sessions, the Workflow Loop, and Resilience](./agent-sessions-and-workflow-loop.md) — how this library's session loop, crash scenarios, heartbeats, and timeouts work against the real `AgentWorkflow` implementation
- [Temporal Concepts: Determinism](https://docs.temporal.io/workflows#determinism)
- [Temporal SDK: Activity Execution](https://docs.temporal.io/activities)
- [Workflow History](https://docs.temporal.io/workflows#history)
- [Continue-as-New Pattern](https://docs.temporal.io/workflows#continue-as-new)

---

_Last updated: 2026-09-05_
