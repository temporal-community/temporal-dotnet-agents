# Durability and Determinism in Temporal Agent Workflows

This document explains how Temporal's durability and determinism guarantees work in the context of agent orchestration workflows, particularly when workers crash or are interrupted.

## Quick Answer

**When an orchestrating workflow with agent calls crashes and restarts:**
- ✅ Completed `agent.RunAsync()` calls are **durable** — they are **NOT re-executed**
- ✅ A new worker will **deterministically replay** from history and **return cached results**
- ✅ The workflow continues from where it left off (or from the last safe checkpoint)
- ⚠️ An agent turn that was *in flight* re-runs its failed activity attempt, so the LLM is called
  again and billed again. Only *completed* activities replay from cache.
- ⚠️ History carried across a continue-as-new boundary is **trimmed by default** — see
  [Continue-as-New and History Carryover](#3-continue-as-new-and-history-carryover).

---

## Temporal's Determinism Guarantee

Temporal workflows are designed to be **deterministic** — they must produce the same result every time they're replayed. This is achieved through:

1. **Event Sourcing**: Every workflow action (activity execution, decision, etc.) is recorded in an immutable event history
2. **Deterministic Replay**: When a workflow resumes after a crash, it replays from the beginning, but returns cached results from history instead of re-executing side effects
3. **Activity Idempotence**: from the workflow's perspective a given `ExecuteActivityAsync` call
   yields exactly one result, but the activity body may execute **several times** across retry
   attempts. Anything with a side effect — a write tool, a notification — must therefore be
   idempotent, or be registered with `opts.NoRetry()` so it gets `MaximumAttempts = 1`.

---

## Agent Call Durability: Step-by-Step

### Initial Execution

```csharp
// using static TemporalCommunity.Extensions.Agents.WorkflowAgents;

[WorkflowRun]
public async Task<string> RunAsync(string userQuestion)
{
    var agent = GetTemporalAgent("WeatherAssistant");
    var session = await agent.CreateSessionAsync();

    // First agent call — dispatches RunDurableAgentStep (Activity1)
    var response1 = await agent.RunAsync(userQuestion, session);

    // Second agent call — dispatches RunDurableAgentStep (Activity2)
    var response2 = await agent.RunAsync("Any follow-up?", session);

    // Regular activity call (Activity3)
    var otherResult = await Workflow.ExecuteActivityAsync(
        (OtherActivities a) => a.DoSomethingAsync(),
        new() { StartToCloseTimeout = TimeSpan.FromMinutes(1) });

    return $"{response1.Text}|{response2.Text}|{otherResult}";
}
```

Two things about this example are load-bearing:

- `GetTemporalAgent` is `WorkflowAgents.GetTemporalAgent` and is **workflow-context only**. Calling
  it outside a `[Workflow]` throws; external callers use `services.GetTemporalAgentProxy("Name")`.
- Each `agent.RunAsync` above happens to converge in one LLM step, so it maps to one activity. A
  turn that calls tools dispatches more — see [Under the Hood](#under-the-hood-how-agent-calls-become-activities).

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

An external caller (API server, console app, `TemporalAIAgentProxy`) goes through `DefaultTemporalAgentClient`, which owns the Temporal client and starts/reuses the session workflow:

```
External Caller (e.g. TemporalAIAgentProxy)
    ↓
    ITemporalAgentClient.SendAsync(sessionId, request)
    ↓
    DefaultTemporalAgentClient.SendAsync()
    ↓
    client.ExecuteUpdateWithStartWorkflowAsync<AgentWorkflow, AgentResponse>(
        wf => wf.RunAgentAsync(request),
        new WorkflowUpdateWithStartOptions(startOp))
        ← ONE atomic RPC. The start operation carries
          IdConflictPolicy = UseExisting and IdReusePolicy = AllowDuplicate,
          so it creates the session workflow or no-ops, and delivers the
          update in the same call. Targets by workflow ID, so it follows
          the continue-as-new chain. Blocks until the handler returns.
    ↓
    AgentWorkflow [WorkflowUpdate("Run")]
        ← Crosses the input-readiness barrier, then serializes via
          _isProcessing and records the request in history
    ↓
    Loop: Workflow.ExecuteActivityAsync(AgentActivities.RunDurableAgentStepAsync)
        ← One LLM call per dispatch
        ← For each surviving tool call, fan out via Workflow.WhenAllAsync over
          AgentActivities.InvokeAgentToolAsync (one activity per tool), preceded
          by RunToolInterceptor activities when an interceptor is configured
    ↓
    AgentActivities.RunDurableAgentStepAsync [Activity]
        ← ResolveBlueprint(agentName) → AgentBlueprint, built lazily on first
          dispatch and then cached. It holds tools + registration + options only.
        ← Everything live — chat client, context providers, interceptor,
          ChatClientAgent, middleware — is resolved fresh from a new DI scope
          on every attempt.
        ← The model call goes through agent.RunStreamingAsync, not through
          IChatClient directly, so user middleware fires around it.
    ↓
    Final AgentResponse returned to AgentWorkflow → recorded in history
    ↓
    Update response returned to DefaultTemporalAgentClient → returned to caller
```

### Path B — Orchestrating Workflow → Sub-Agent (via `TemporalAIAgent`)

Inside an orchestrating `[Workflow]`, `GetTemporalAgent()` returns a `TemporalAIAgent` that dispatches inference by calling `Workflow.ExecuteActivityAsync` directly — without starting a separate session workflow:

```
Orchestrating [Workflow] (e.g. ResearchWorkflow)
    ↓
    var agent = GetTemporalAgent("ResearcherAgent");
    var session = await agent.CreateSessionAsync();   // a TemporalAgentSession
    await agent.RunAsync(messages, session)
    ↓
    TemporalAIAgent.RunCoreAsync()
        ← Rejects a session that is not a TemporalAgentSession, or whose
          SessionId.AgentName belongs to a different agent
        ← EnterRun() — two overlapping RunAsync calls on the SAME session throw
        ← session.AppendHistoryEntry(AgentSessionRequest.FromRunRequest(...))
          The SESSION owns the history. TemporalAIAgent holds no _history field;
          the agent is stateless with respect to the conversation, which is what
          lets one agent drive several sessions in the same workflow.
    ↓
    Drive the durable loop in-place, seeded from session.History:
      Workflow.ExecuteActivityAsync(
        (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
        activityOptions)
        ← stepInput.SerializedStateBag comes from session.SerializeStateBag()
        ← Activity result is recorded in the ORCHESTRATING workflow's event history
        ← session.OverlayTrustedStateBag(stepResult.UpdatedStateBag)
      Per surviving tool call:
        Workflow.ExecuteActivityAsync(
          (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
          toolOptions)
        ← then session.MergeToolStateBagWriteBacks(...) in tool-call index order
    ↓
    session.AppendHistoryEntry(AgentSessionResponse.FromAgentResponse(...))
    ExitRun()
    ↓
    AgentResponse returned to orchestrating workflow code
```

Because the conversation lives on the session object rather than in a separate workflow, an
orchestrating workflow that continues-as-new must carry the serialized session forward itself. The
library does not do this for you — see
[Agent Sessions and the Workflow Loop](./agent-sessions-and-workflow-loop.md#temporalagentsession-bridging-two-worlds).

### Why This Ensures Durability

1. **Activity Results are History**: The `AgentResponse` is recorded as an activity completion event in whichever workflow scheduled it
2. **History is Immutable**: Once recorded, the event cannot be changed
3. **Replay is Deterministic**: Future replays of the workflow retrieve the cached result without re-executing
4. **Session Workflow is Separate — Path A only**: when the session is driven by an external
   client, `AgentWorkflow` maintains its own independent history and state. On Path B there is no
   second workflow; the agent's activity results are recorded directly in the orchestrating
   workflow's history, and the conversation lives on the `TemporalAgentSession` object held in that
   workflow's state.
5. **Everything live is rebuilt per attempt**: only the `AgentBlueprint` (tool registry +
   registration + options) is cached across calls. The chat client, context providers, tool
   interceptor, `ChatClientAgent` and user middleware are resolved from a fresh DI scope on every
   attempt, so a retry never reuses a stale or captive scoped dependency.

---

## Important Nuances

### 1. Activity Retries vs. Workflow Replays

| Concept | Scope | Retry Behavior |
|---------|-------|---|
| **Activity Retry** | Across attempts of one `ExecuteActivityAsync` call | Temporal retries internally, under `ActivityOptions.RetryPolicy`. The workflow's `await` does not throw until the budget is exhausted. |
| **Workflow Replay** | Across workflow task executions | Results come from history; completed activities are never re-executed |

The library never leaves `RetryPolicy` null on an activity it builds. A null policy is transmitted
as "server default", which means `MaximumAttempts = 0` — *unlimited* retries — so a deterministically
failing LLM call would retry forever and hang the caller. `DefaultRetryPolicy` substitutes a bounded
default instead, on every dispatch path: the session loop's model step, `TemporalAIAgent`'s
sub-agent step, and tool/policy activities. The defaults are five attempts, with a `2s` maximum
backoff interval for model steps and `30s` for tool work.

A caller-supplied `ActivityOptions` is used **verbatim**, including a null `RetryPolicy` — at that
point it is the caller's own choice.

### 2. Session Workflow Durability is Separate — but only on Path A

`AgentWorkflow` is durable independently of any workflow that talks to it. Nothing *inside* an
activity starts it: only a client does, through `DefaultTemporalAgentClient`'s Update-With-Start.

```
Path A — external client owns the session:

  Client process
      ↓  ExecuteUpdateWithStartWorkflowAsync (one RPC)
  AgentWorkflow [History B] ← its own independent history

  If either crashes:
    - Client: reconnects and re-sends; _isProcessing serializes duplicates
    - AgentWorkflow: resumed from History B
```

```
Path B — orchestrating workflow, NO second workflow:

  Orchestrating Workflow [History A]
      ↓  Workflow.ExecuteActivityAsync(RunDurableAgentStep / InvokeAgentTool)
  Activity results land in History A

  If the worker crashes:
    - Orchestrating Workflow: replayed from History A; completed agent steps
      return cached results
```

On Path B there is no `AgentWorkflow` and no second history. The sub-agent's durability *is* the
orchestrating workflow's durability, which is why the orchestrator must carry the serialized
`TemporalAgentSession` across its own continue-as-new if the conversation should survive.

### 3. Continue-as-New and History Carryover

`AgentWorkflow` extends the shared `DurableChatWorkflowBase<AgentResponse>` (from
`TemporalCommunity.Extensions.AI`), which owns the wait/trigger loop for continue-as-new — it is
not inline in `AgentWorkflow`.

**There are exactly two CAN triggers**, and both additionally require that no turn currently holds
the serialization gate:

- `Workflow.ContinueAsNewSuggested` — the SDK's own signal that history is approaching its limits
- `_history.Count >= MaxEntryCount` — the library's entry-count trigger (default `1000`)

**Shutdown is not a trigger.** The rollover branch is gated on `&& !_shutdownRequested`, so a
`Shutdown` signal ends the run instead of rolling it over. TTL expiry likewise completes the
workflow rather than continuing it.

When a trigger fires, the base reduces the history, waits for `Workflow.AllHandlersFinished`, and
invokes the `CreateContinueAsNewException` hook that `AgentWorkflow` overrides to build the
MAF-specific carried input (StateBag snapshot, agent approval archive, and the base-class CAN
fields).

**Carry-forward is lossy by default — history is not fully preserved.** With no
`HistoryReducerKey` configured, the base applies `DefaultBoundedTrim`, which keeps only the most
recent `MaxEntryCount / 2` entries (floored, minimum 1) and additionally drops a leading orphaned
response whose matching request was just cut. The half-window is deliberate: carrying the full
history forward would immediately re-trip the same entry-count threshold and produce a back-to-back
CAN loop. Configure a `HistoryReducerKey` when a smarter retention policy is required.

So the accurate statement is: **the run rolls over transparently, the workflow ID is unchanged, the
StateBag and approval archive are preserved in full, and an unpinned handle automatically follows
the chain — but older conversation entries may be gone.** Do not treat `GetHistory` as a complete
transcript across a CAN boundary, and do not rely on the workflow as the system of record for a
transcript that must be retained; externalize it.

For the exact override code and the full field list carried forward, see
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

- **Trust Temporal's replay mechanism** — *completed* activities in history are not re-executed
- **Design tools to be idempotent** — the activity body runs once per retry attempt, so a tool that
  is not idempotent must be registered with `opts.NoRetry()` to get `MaximumAttempts = 1`
- **Set timeouts per workload** — `StartToCloseTimeout` bounds one attempt, not the whole call;
  size `HeartbeatTimeout` against how often the activity actually heartbeats
- **Externalize a transcript you must keep** — continue-as-new trims carried history by default
- **Monitor workflow history** — use Temporal CLI/UI to inspect event history after crashes, and
  check `attempt` on `ActivityTaskStarted` to see whether a body ran more than once
- **Replay-test the workflow** — run captured histories through `WorkflowReplayer` after any change
  to the dispatch loop

### ❌ DON'T

- **Confuse "one result" with "one execution"** — a single `ExecuteActivityAsync` call yields one
  result to the workflow, but its activity body can run once per retry attempt
- **Change a shipped workflow's logic in place** — gate the change behind `Workflow.Patched(id)` and
  retire it with `Workflow.DeprecatePatch(id)`. These are the .NET SDK's versioning primitives;
  `Workflow.GetVersion` is the Go/Java API and does not exist here.
- **Reach for ambient randomness** — use `Workflow.Random` / `Workflow.NewGuid()`, never
  `System.Random` or `Guid.NewGuid()`
- **Hash with `string.GetHashCode()` inside a workflow** — .NET randomizes it per process, so two
  runs disagree and diverge on replay. Use a fixed-seed hash.
- **Start an `ActivitySource` span inside `[Workflow]` code** — non-deterministic on replay; the
  `agent.turn` span belongs in the activity
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

Useful things to look for in that output:

- `ActivityTaskScheduled` / `ActivityTaskCompleted` pairs per LLM step and per tool call. The
  `activityType` field carries the fully-qualified activity name
  (`TemporalCommunity.Extensions.Agents.RunDurableAgentStep`,
  `...InvokeAgentTool`, `...RunToolInterceptor`, `...ReduceHistoryByKey`), so the dispatch shape of
  a turn is readable directly from history.
- `attempt` on `ActivityTaskStarted` — greater than 1 means the activity body ran more than once.
- `WorkflowExecutionContinuedAsNew` — the rollover point. The new run's `WorkflowExecutionStarted`
  input carries `CarriedHistory`, and comparing its length to the previous run's entry count shows
  the trim.

### In Code: Testing Durability

The replay-safety check the library itself relies on is `WorkflowReplayer` over captured histories:
feed a real event history back through the workflow definition and assert it replays without a
`NonDeterministicWorkflowException`. That is what catches a determinism regression in the dispatch
loop, and it is why any change to the loop's shape needs a regenerated history corpus. The pattern
lives in `tests/TemporalCommunity.Extensions.Agents.Tests/Compat/AgentWorkflowReplayTests.cs`.

For unit-testing agent behaviour (as opposed to replay safety), see
[testing-agents.md](../../how-to/MAF/testing-agents.md).

---

## Summary Table

| Question | Answer |
|----------|--------|
| Are completed agent calls durable? | ✅ Yes — recorded in event history |
| Will completed activities re-run after a worker crash? | ❌ No — cached results are returned |
| Will an *in-flight* LLM call be re-issued after a crash? | ✅ Yes — the failed attempt is retried, so the model is called and billed again |
| Is the session workflow separately durable? | ✅ On Path A — `AgentWorkflow` has its own history. ❌ On Path B — there is no second workflow; the sub-agent's activities live in the orchestrator's history |
| Can a workflow resume after partial completion? | ✅ Yes — from the last checkpoint |
| Will conversation history be lost on crash? | ❌ No — reconstructed on replay from workflow state |
| Will conversation history be lost at continue-as-new? | ⚠️ Partially — `DefaultBoundedTrim` keeps only the most recent `MaxEntryCount / 2` entries unless a `HistoryReducerKey` is configured |
| Is the StateBag preserved across continue-as-new? | ✅ Yes — carried in full as `CarriedStateBag`, with a 64 KB warning threshold |
| Is the StateBag preserved when a turn fails? | ✅ Yes — rolled back to its pre-turn snapshot, retaining reserved approval-scope records |
| Should activities be idempotent? | ✅ Yes — the body can run once per retry attempt. Use `opts.NoRetry()` for write tools |
| What if an activity fails and is retried? | The workflow's `await` does not throw until the retry budget is exhausted (bounded to 5 attempts by default) |
| Does a `Shutdown` signal cause a continue-as-new? | ❌ No — the rollover branch is gated on `!_shutdownRequested`; shutdown ends the run |

---

## References

- [Agent Sessions, the Workflow Loop, and Resilience](./agent-sessions-and-workflow-loop.md) — how this library's session loop, crash scenarios, heartbeats, and timeouts work against the real `AgentWorkflow` implementation
- [Temporal Concepts: Determinism](https://docs.temporal.io/workflows#determinism)
- [Temporal SDK: Activity Execution](https://docs.temporal.io/activities)
- [Workflow History](https://docs.temporal.io/workflows#history)
- [Continue-as-New Pattern](https://docs.temporal.io/workflows#continue-as-new)
