# Agent Sessions, the Workflow Loop, and Resilience

This document explains how `TemporalAgentSession` bridges the Microsoft Agent Framework and Temporal, how the agent execution loop works inside `AgentWorkflow`, how `WorkflowUpdate` delivers messages, and how the system handles crashes, heartbeats, and timeouts.

---

## Table of Contents

1. [TemporalAgentSession: Bridging Two Worlds](#temporalagentsession-bridging-two-worlds)
2. [The Agent Loop Inside AgentWorkflow](#the-agent-loop-inside-agentworkflow)
3. [Sending Messages via WorkflowUpdate](#sending-messages-via-workflowupdate)
4. [Durable Agent Composition](#durable-agent-composition)
5. [Durable Agent Workflow Loop](#durable-agent-workflow-loop)
6. [Crashes, Heartbeats, and Timeouts](#crashes-heartbeats-and-timeouts)

---

## TemporalAgentSession: Bridging Two Worlds

### The Problem

The **Microsoft Agent Framework** (`Microsoft.Agents.AI`) uses an `AgentSession` to track conversation state between turns. Sessions are short-lived, in-memory objects — they have no built-in persistence model.

**Temporal**, on the other hand, models long-lived processes as *workflows*. Every workflow has a globally unique workflow ID and an immutable event history. Workflow state survives process crashes and is replayed deterministically.

The challenge: make a Microsoft Agent Framework session **durable** by tying it to a Temporal workflow, without either framework knowing about the other.

### The Solution: TemporalAgentSessionId

`TemporalAgentSessionId` is a `readonly struct` that encodes a session's identity as a Temporal workflow ID:

```
Format: ta-{agentName}-{key}

Examples:
  ta-weatherassistant-a1b2c3d4e5f6...     (random key, from proxy)
  ta-weatherassistant-7f8a9b0c1d2e...     (deterministic key, from workflow)
```

The struct has two factory methods, and the choice between them is critical for **workflow determinism**:

| Factory | Key Source | Used By | Why |
|---------|-----------|---------|-----|
| `WithRandomKey(agentName)` | `Guid.NewGuid()` | `TemporalAIAgentProxy` (external callers) | External callers run outside workflows — randomness is safe |
| `WithDeterministicKey(agentName, guid)` | `Workflow.NewGuid()` | `TemporalAIAgent` (inside workflows) | Workflow code must be deterministic — `Workflow.NewGuid()` returns the same GUID on replay |

This distinction exists because Temporal replays workflow code from history. If a workflow used `Guid.NewGuid()`, it would generate a *different* GUID on replay, breaking determinism and causing a non-determinism error. `Workflow.NewGuid()` is replay-safe.

### TemporalAgentSession

`TemporalAgentSession` extends the framework's `AgentSession`, wraps a `TemporalAgentSessionId`,
and **owns the conversation**:

```csharp
public sealed class TemporalAgentSession : AgentSession
{
    public TemporalAgentSessionId SessionId { get; }

    // Conversation state lives here, not on the agent. Internal by design —
    // exposing conversation content publicly is a separate, unmade decision.
    internal IReadOnlyList<DurableSessionEntry> History { get; }   // read-only facade
    internal void AppendHistoryEntry(DurableSessionEntry entry);
    internal void RestoreHistory(IEnumerable<DurableSessionEntry> entries);

    // StateBag is inherited from AgentSession.
    internal JsonElement? SerializeStateBag();
    internal void OverlayTrustedStateBag(JsonElement? updated);

    // Service locator pattern — allows agents and tools to discover the session ID
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
            return this.SessionId;
        return base.GetService(serviceType, serviceKey);
    }
}
```

Nothing conversational lives on the agent instance, so one agent can drive many sessions
concurrently. Two consequences follow:

- `TemporalAIAgent.RunAsync` **requires** a `TemporalAgentSession` whose `SessionId.AgentName`
  matches the agent; a foreign `AgentSession` is rejected.
- State survives continue-as-new only if the orchestrating workflow explicitly carries the
  serialized session forward. The library does not guess.

**Key behaviors:**

- **Serialization**: the wire shape is the internal `TemporalAgentSessionSnapshot` DTO, not the
  session type itself: `{ "sessionId": "ta-name-key", "stateBag": { ... }, "history": [ ... ] }`.
  `stateBag` and `history` are omitted when empty, and a snapshot with no `history` member
  restores as an empty history. `TemporalAgentSession` is deliberately *not* registered in
  `AgentSessionJsonContext` — serialize through the snapshot.
- **Implicit conversion**: `TemporalAgentSessionId` converts implicitly *to* `string`, so it can be
  passed anywhere a workflow ID is expected. The reverse direction is explicit —
  `TemporalAgentSessionId.Parse(workflowId)`.
- **ToString**: Returns the workflow ID, making it easy to log and debug.
- **Run guard**: `EnterRun` / `ExitRun` reject two overlapping `RunAsync` calls on the same
  session instance. Parallel conversations on one agent are fine — give each its own session.

### How Session Maps to Workflow

The mapping is 1:1:

```
TemporalAgentSession
    └─ SessionId: TemporalAgentSessionId
         └─ WorkflowId: "ta-weatherassistant-a1b2c3d4"
              └─ Maps to: AgentWorkflow instance with this workflow ID
```

When `DefaultTemporalAgentClient` receives a session ID it issues a single atomic
**Update-With-Start** (`ExecuteUpdateWithStartWorkflowAsync`) whose start operation carries
`IdConflictPolicy = UseExisting` and `IdReusePolicy = AllowDuplicate`. This means:
- **First call**: creates a new `AgentWorkflow` with this workflow ID *and* delivers the turn in
  one RPC, so there is no client-crash window between start and update
- **Subsequent calls**: the start operation no-ops — the workflow already exists — and only the
  update is delivered
- **Targeting**: the operation targets the workflow *ID*, so it follows the continue-as-new chain

Fire-and-forget turns (immediate and delayed) use the equivalent **Signal-With-Start** shape.

The session effectively *is* the workflow. Creating a session doesn't start the workflow; the first `RunAsync` call does.

---

## The Agent Loop Inside AgentWorkflow

### Inheritance: shared session loop, MAF-specific overrides

`AgentWorkflow` inherits from `DurableChatWorkflowBase<AgentResponse>` (declared in
`TemporalCommunity.Extensions.AI`). The base class owns the session-loop body — the turn
mutex, turn rollback, continue-as-new triggering, history reduction, the
`[WorkflowQuery("GetHistory")]` handler, the `[WorkflowSignal("Shutdown")]` handler, and the
generic HITL approval handlers (`RequestApproval`, `ResolveApproval`, `GetPendingApproval`).

**Wire names.** The turn Update is named `Run`; the graceful-shutdown signal is named `Shutdown`.
These are the strings a raw Temporal client must send. Prefer
`ITemporalAgentClient.ShutdownAsync`, or the
`DurableChatWorkflowBase<AgentResponse>.ShutdownSignalName` constant, over hard-coding the signal
name.

The shared shape:

```
DurableChatWorkflowBase<TOutput>           ← in TemporalCommunity.Extensions.AI
    ├─ _history: List<DurableSessionEntry> (private)
    ├─ session-loop body (turn mutex, turn rollback, CAN trigger, history reducer)
    ├─ [WorkflowQuery("GetHistory")]
    ├─ [WorkflowSignal("Shutdown")]        ← ShutdownSignalName
    ├─ [WorkflowUpdate("RequestApproval")] + [WorkflowUpdateValidator]
    ├─ [WorkflowUpdate("ResolveApproval")]
    ├─ [WorkflowQuery("GetPendingApproval")]
    ├─ protected abstract BuildResponseEntry(...)          ← MAF override below
    ├─ protected abstract ExecuteTurnAsync(...)            ← MAF override below
    ├─ protected abstract CreateContinueAsNewException(...) ← MAF override below
    ├─ protected virtual UpsertCustomSearchAttributes()    ← MAF override below
    ├─ protected virtual OnApprovalResolutionAccepted(...)  ← MAF override below
    └─ protected virtual OnApprovalRequestResolved(...)     ← MAF override below

AgentWorkflow : DurableChatWorkflowBase<AgentResponse>     ← in TemporalCommunity.Extensions.Agents
    ├─ _currentStateBag: JsonElement?  (MAF-specific)
    ├─ _input: AgentWorkflowInput?     (MAF-specific)
    ├─ [WorkflowUpdate("Run")] + [WorkflowUpdateValidator]
    ├─ [WorkflowUpdate("GrantSessionApprovalScope")]   ← opt-in scope administration
    ├─ [WorkflowUpdate("RevokeSessionApprovalScope")]  ← opt-in scope administration
    ├─ [WorkflowSignal("RunFireAndForget")]
    ├─ IDurableTurnRollbackParticipant (StateBag capture/restore per turn)
    ├─ override BuildResponseEntry → AgentSessionResponse.FromAgentResponse(...)
    ├─ override ExecuteTurnAsync   → drives ExecuteDurableAgentTurnAsync (the durable loop)
    ├─ override CreateContinueAsNewException → carries _currentStateBag forward
    └─ override UpsertCustomSearchAttributes → upserts AgentName
```

Reusable approval **scopes** are a MAF addition, but they are not a separate wire update.
`AgentWorkflow.ResolveAgentApprovalAsync` is `internal`: it is reached either from the privileged
`GrantSessionApprovalScope` update, or from the inherited `ResolveApproval` update via the
`OnApprovalResolutionAccepted` / `OnApprovalRequestResolved` hooks, which upgrade the base's
scope-less decision into the full `DurableAgentApprovalDecision` identity before archiving it.

`AgentWorkflowInput` itself inherits from `DurableChatWorkflowInput`, so the
shared fields (`MaxEntryCount`, `HistoryReducerKey`, `EnableSearchAttributes`, `ActivityTimeout`,
`HeartbeatTimeout`, `RetryPolicy`, `TimeToLive`) come from the base, while MAF-only fields
(`AgentName`, `TaskQueue`, `CarriedStateBag`, `ResolvedWorkerConfig`,
`AgentApprovalResolutionHistory`) live on the subclass.

### Lifecycle Overview

`AgentWorkflow` is the durable backbone of every agent session. It is a long-lived Temporal workflow that:

1. **Starts** when the first message is sent to an agent session
2. **Waits** for incoming messages (via Update or Signal)
3. **Dispatches** each message to an activity that runs the real AI agent
4. **Accumulates** conversation history as workflow state
5. **Continues-as-new** when the SDK suggests it *or* when `_history.Count` reaches
   `MaxEntryCount` (default `1000`)
6. **Shuts down** when a `Shutdown` signal arrives or the TTL expires

Steps 2, 4, 5, and 6 are implemented by the base class. Step 3 is the
subclass's `ExecuteTurnAsync` override (which dispatches `AgentActivities`
rather than `DurableChatActivities`).

Shutdown and continue-as-new are mutually exclusive: the CAN branch is gated on
`!_shutdownRequested`, so a shutdown signal ends the run rather than rolling it over.

### The Main Run Loop

`AgentWorkflow.RunAsync` is a thin shim that wires up the MAF-specific
state and then delegates to the base:

```csharp
[WorkflowRun]
public async Task RunAsync(AgentWorkflowInput input)
{
    ArgumentNullException.ThrowIfNull(input);
    InitializeInput(input);                     // Unblocks the readiness barrier
    _input = input;                             // MAF-only: typed view of the input
    _currentStateBag = input.CarriedStateBag;   // MAF-only: restore StateBag
    RestoreResolvedAgentApprovals(input.AgentApprovalResolutionHistory);

    Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);

    await base.RunAsync(input).ConfigureAwait(true);   // Base owns the loop
}
```

Inside the base, the loop looks like this (paraphrased — see
`DurableChatWorkflowBase<TOutput>` for the canonical implementation):

```csharp
// In DurableChatWorkflowBase<TOutput>.RunAsync(DurableChatWorkflowInput input):
InitializeInput(input);
if (input.CarriedHistory is { Count: > 0 })
    _history.AddRange(input.CarriedHistory);        // Restore from CAN
_turnCount = InitializeTurnCount(_history);         // Re-derive from history

var sessionCreatedAt = input.OriginalCreatedAt ?? Workflow.UtcNow;

if (input.EnableSearchAttributes)
{
    Workflow.UpsertTypedSearchAttributes(/* SessionCreatedAt, TurnCount */);
    UpsertCustomSearchAttributes();                 // Subclass hook (MAF: AgentName)
}

// input.TimeToLive is a non-nullable TimeSpan that already defaults to 14 days.
bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested
          || (!_isProcessing && Workflow.ContinueAsNewSuggested)
          || (!_isProcessing && _history.Count >= input.MaxEntryCount),
    timeout: input.TimeToLive);

if (!conditionMet)
{
    // TTL elapsed. Drain in-flight handlers (e.g. fire-and-forget turns) so they
    // are not aborted with TMPRL1102, then complete normally.
    await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
    return;
}

if ((Workflow.ContinueAsNewSuggested || _history.Count >= input.MaxEntryCount)
    && !_shutdownRequested)
{
    var carriedHistory = input.HistoryReducerKey is not null
        ? await ApplyKeyedHistoryReducerAsync(input.HistoryReducerKey, _history, reducerOptions)
        : DefaultBoundedTrim(_history, input.MaxEntryCount);

    var carriedInput = CreateContinueAsNewInput(input, carriedHistory, /* ... */);
    await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
    throw CreateContinueAsNewException(carriedInput);   // Subclass hook
}
```

This is **not** a tight polling loop. `WaitConditionAsync` is an event-driven primitive that parks the workflow until one of these conditions becomes true:

- `_shutdownRequested` — set by the `Shutdown` signal (handler is on the base). This is a
  **terminal** condition: it exits the run; it does not trigger continue-as-new.
- `Workflow.ContinueAsNewSuggested` — set by Temporal when history approaches size limits
- `_history.Count >= input.MaxEntryCount` — the library's own entry-count trigger
- The TTL timeout elapses

Both CAN triggers additionally require `!_isProcessing`, so a rollover never interrupts a turn
that is already inside the gate, and `Workflow.AllHandlersFinished` is awaited before the run
ends so a fire-and-forget turn is never aborted mid-flight.

While the workflow is parked, it is **not consuming compute resources**. It sits in Temporal's persistence layer and only wakes when a message (Update or Signal) arrives.

### The Processing Gate: `_isProcessing`

The base serializes concurrent requests with a boolean gate. The subclass's
`[WorkflowUpdate("Run")]` handler crosses the input-readiness barrier, then delegates the actual
turn execution to the base's `RunTurnAsync` helper, which acquires the gate, appends the
request entry, calls `ExecuteTurnAsync` (the subclass override), appends
the response entry, and releases the gate:

```csharp
// In AgentWorkflow:
[WorkflowUpdate("Run")]
public async Task<AgentResponse> RunAgentAsync(RunRequest request)
{
    await WaitForAgentInputAsync().ConfigureAwait(true);

    var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
    var (output, _) = await RunTurnAsync(requestEntry, chatOptions: null);

    Workflow.Logger.LogWorkflowUpdateCompleted(
        _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
    return output;
}
```

`RunTurnAsync` (on the base) wraps the body in the mutex and treats the turn as a transaction:

```csharp
// In DurableChatWorkflowBase<TOutput>.RunTurnAsync(...):
await Workflow.WaitConditionAsync(() => !_isProcessing);
_isProcessing = true;

var historyCountBeforeTurn = _history.Count;
var turnCountBeforeTurn = _turnCount;
var rollbackParticipant = this as IDurableTurnRollbackParticipant;   // AgentWorkflow implements this
JsonElement? participantStateBeforeTurn = null;
try
{
    // Captured only after this turn owns the gate, so a queued update cannot snapshot
    // state from before the preceding turn committed.
    participantStateBeforeTurn = rollbackParticipant?.CaptureTurnRollbackState();

    _history.Add(requestEntry);
    _turnCount++;

    var activityOptions = new ActivityOptions
    {
        StartToCloseTimeout = RequiredInput.ActivityTimeout,
        HeartbeatTimeout = RequiredInput.HeartbeatTimeout,
        RetryPolicy = DefaultRetryPolicy.ResolveForModel(RequiredInput.RetryPolicy),
        Summary = DurableChatClient.BuildActivitySummary(chatOptions),
    };

    var output = await ExecuteTurnAsync(activityOptions, requestEntry, chatOptions);
    var responseEntry = BuildResponseEntry(requestEntry.CorrelationId, output, Workflow.UtcNow);

    _history.Add(responseEntry);
    return (output, responseEntry);
}
catch
{
    // Roll the whole turn back: request entry, turn count, and the participant's StateBag.
    _history.RemoveRange(historyCountBeforeTurn, _history.Count - historyCountBeforeTurn);
    _turnCount = turnCountBeforeTurn;
    rollbackParticipant?.RestoreTurnRollbackState(participantStateBeforeTurn);
    throw;
}
finally
{
    _isProcessing = false;
}
```

If two Updates arrive simultaneously, the second one **blocks** on `WaitConditionAsync(() => !_isProcessing)` until the first completes. This ensures:

- Conversation history is appended in order
- The activity receives a consistent snapshot of prior messages
- No race conditions on `_history`
- A failed turn leaves no partial request entry, no inflated turn count, and no leaked StateBag
  mutation behind

### MAF-specific subclass hooks

The overrides on `AgentWorkflow`:

| Hook | Purpose |
|---|---|
| `BuildResponseEntry(correlationId, AgentResponse output, createdAt)` | Returns `AgentSessionResponse.FromAgentResponse(...)` so the entry on the wire is the MAF subclass with `OrchestrationId`/`ResponseType`/`ResponseSchema` discrimination preserved. |
| `ExecuteTurnAsync(activityOptions, requestEntry, chatOptions)` | Discards the base's `activityOptions` and builds its own from the frozen `AgentWorkflowInput`, then drives the per-step durable loop in `ExecuteDurableAgentTurnAsync`: each iteration dispatches `AgentActivities.RunDurableAgentStepAsync` (one LLM call) and, if the step returns pending tool calls, fans out one `AgentActivities.InvokeAgentToolAsync` activity per call via `Workflow.WhenAllAsync`. Overlays the updated StateBag onto `_currentStateBag` after each step. |
| `CreateContinueAsNewException(input)` | `with`-clones the retained `_input` (an `AgentWorkflowInput`), overwriting the base-owned CAN fields from the `input` argument and adding `CarriedStateBag` plus `AgentApprovalResolutionHistory`. Cloning rather than field-by-field reconstruction is what keeps a newly added setting from being silently dropped at the boundary. |
| `UpsertCustomSearchAttributes()` | Upserts the `AgentName` typed search attribute. Called by the base after the standard `SessionCreatedAt` / `TurnCount` upserts. Default in the base is a no-op; `DurableChatWorkflow` (the MEAI sibling) does not override it because chat sessions are not named. |
| `ApplyKeyedHistoryReducerAsync(reducerKey, history, activityOptions)` | Dispatches the `ReduceHistoryByKey` activity so the DI-resolved reducer delegate runs on the worker and its result lands in workflow history. |
| `OnApprovalResolutionAccepted` / `OnApprovalRequestResolved` | Upgrade the base's scope-less `DurableApprovalDecision` into the full `DurableAgentApprovalDecision` (scope, pattern, grant ID, expiry) before it enters the MAF approval archive. |
| `CaptureTurnRollbackState` / `RestoreTurnRollbackState` (`IDurableTurnRollbackParticipant`) | Snapshot and restore `_currentStateBag` inside the base's serialized-turn gate. Restoration retains reserved approval-scope records, which were committed by independent approval updates while the turn was parked. |

The fire-and-forget path is unique to MAF and stays on the subclass:

```csharp
[WorkflowSignal("RunFireAndForget")]
public Task RunAgentFireAndForgetAsync(RunRequest request) { /* ... */ }
```

Signals do not return a value to the caller, so this handler kicks off a
detached task that follows the same pattern as `RunAgentAsync` but with no
return path. It uses the same `RunTurnAsync` helper internally.

The default client starts new sessions atomically: synchronous turns use
Update-With-Start, while immediate and delayed fire-and-forget turns use
Signal-With-Start. Temporal may admit those first handlers before the workflow run task has
initialized `AgentWorkflowInput`, so both handler paths cross an internal deterministic readiness
barrier before reading or changing turn state. The barrier completes synchronously for established
sessions and does not schedule a timer or activity.

Custom workflow Update validators remain synchronous and cannot wait for initialization. A validator
may reject malformed request data that does not depend on workflow input. Initialization-dependent
checks belong in the Update handler after its own deterministic readiness barrier.

### Conversation History as Workflow State

Every request/response pair is recorded in `_history` as a `DurableSessionEntry`. The MAF library
stores instances of two concrete subclasses (`AgentSessionRequest` / `AgentSessionResponse`),
which extend the AI library's shared `DurableSessionRequest` / `DurableSessionResponse`:

```
_history: List<DurableSessionEntry>
[
    AgentSessionRequest  { correlationId: "abc", messages: [ChatMessage(User, "Hi")] },
    AgentSessionResponse { correlationId: "abc", messages: [ChatMessage(Assistant, "Hello!")], usage: {...} },
    AgentSessionRequest  { correlationId: "def", messages: [ChatMessage(User, "Weather?")] },
    AgentSessionResponse { correlationId: "def", messages: [ChatMessage(Assistant, "It's sunny")], usage: {...} },
]
```

The runtime polymorphism modifier in `TemporalAgentJsonUtilities` registers the
MAF subclasses with the discriminator strings `"agent_request"` and
`"agent_response"`; the AI library's own concrete types use `"ai_request"` /
`"ai_response"`. All four shapes round-trip through `DurableAIDataConverter`.

Each entry contains:

| Field | Where defined | Purpose |
|-------|---------------|---------|
| `CorrelationId` | `DurableSessionEntry` (shared) | Links a request to its response. Caller-supplied via `TemporalAgentRunOptions.CorrelationId` or auto-generated with `Workflow.NewGuid()` |
| `CreatedAt` | `DurableSessionEntry` (shared) | Timestamp for ordering (`Workflow.UtcNow`) |
| `Messages` | `DurableSessionEntry` (shared) | `IReadOnlyList<ChatMessage>` — MEAI types stored directly (user text, assistant text, tool calls, tool results); polymorphism preserved by `DurableAIDataConverter` |
| `Usage` (response only) | `DurableSessionResponse` (shared) | `Microsoft.Extensions.AI.UsageDetails` — token counts from the LLM, stored directly with no wrapper |
| `OrchestrationId` (request only) | `AgentSessionRequest` (MAF-specific) | Workflow ID of the orchestrating workflow, if this was a sub-agent call |
| `ResponseType` / `ResponseSchema` (request only) | `AgentSessionRequest` (MAF-specific) | Structured-output format hint preserved across replay |

When a turn starts, `ExecuteDurableAgentTurnAsync` flattens the history into a working `accumulated` list of `ChatMessage` objects. That list is the seed for the per-step loop: each `RunDurableAgentStep` activity receives it as `AgentStepInput.AccumulatedMessages`, the LLM sees the full conversation on every iteration, and the workflow appends each step's assistant message (and any tool-result message) back onto the list before the next iteration. Because `entry.Messages` is already `IReadOnlyList<ChatMessage>`, no conversion step is needed:

```csharp
// Inside AgentWorkflow.ExecuteDurableAgentTurnAsync, before the loop:
var accumulated = FlattenHistoryMessages();   // History → flat List<ChatMessage>

// accumulated now contains: [User: "Hi", Assistant: "Hello!", User: "Weather?"]
// Each iteration re-sends this (plus any in-turn assistant/tool messages) to the LLM
```

Note the turn's own request entry is already in history by this point — the base's `RunTurnAsync`
appended it before calling `ExecuteTurnAsync` — so the flattened list is the full conversation
*including* the current prompt. Nothing is passed separately.

### Continue-as-New: History Carryover

Temporal workflows have a practical limit on event history size (typically ~50K events). Rollover
fires when `Workflow.ContinueAsNewSuggested` becomes true, or when `_history.Count` reaches
`MaxEntryCount` (default `1000`) — whichever comes first, and only while no turn holds the gate
and no shutdown has been requested. The workflow then:

1. **Reduces** the history (base does this). Two paths:
   - `HistoryReducerKey` set → dispatch the `ReduceHistoryByKey` activity and carry its result
   - otherwise → `DefaultBoundedTrim`
2. Waits for `Workflow.AllHandlersFinished` so no in-flight turn is aborted
3. Calls `CreateContinueAsNewException(carriedInput)` (subclass override produces the typed exception)
4. The MAF override `with`-clones `_input`, overwriting the base CAN fields from the argument and
   adding `CarriedStateBag = _currentStateBag` and the MAF approval archive, then returns
   `Workflow.CreateContinueAsNewException((AgentWorkflow wf) => wf.RunAsync(carriedInput))`
5. Temporal starts a **new run** of the same workflow ID
6. The new run restores history from `input.CarriedHistory` (in the base) and `_currentStateBag` from `input.CarriedStateBag` (in `AgentWorkflow.RunAsync`)
7. The base's `InitializeTurnCount` re-derives `_turnCount` from the carried history (counting `DurableSessionResponse` entries), so the `TurnCount` search attribute monotonically grows across CAN boundaries

**Carry-forward is lossy by default.** `DefaultBoundedTrim` keeps only the most recent
`MaxEntryCount / 2` entries (floored, minimum 1) and returns the history unchanged only when it is
already at or below that target. The half-window is deliberate: carrying the full history into the
fresh run would immediately re-trip the same entry-count threshold and produce a back-to-back CAN
loop. The trim additionally drops a leading orphaned response whose matching request was just cut,
so the new run never opens on a response it cannot attribute. `MaxEntryCount` is validated to be at
least `4` precisely so a complete request/response turn can survive the half-window.

Configure a `HistoryReducerKey` when a smarter policy (summarisation, selective retention) is
required; the delegate is resolved from DI inside the activity, so only the *key* travels on the
wire.

From the caller's perspective the workflow ID is unchanged and the conversation continues, but
**older turns may be gone** — do not treat `GetHistory` as a complete transcript across CAN
boundaries. The StateBag carry-forward is the MAF-specific piece; everything else is shared with
`DurableChatWorkflow`.

**StateBag size guard.** `CreateContinueAsNewException` measures the serialized `CarriedStateBag`
and emits a `LogWarning` when it exceeds 64 KB. It is a warning only — the session keeps running —
but it is the signal to prune or externalize StateBag contents.

---

## Sending Messages via WorkflowUpdate

### Why Updates Instead of Signal + Query

The traditional Temporal pattern for request/response is:

```
Client → Signal(request) → Workflow processes → Client polls Query until result ready
```

This works but requires a polling loop on the client side. `WorkflowUpdate` provides a synchronous alternative:

```
Client → Update(request) → Workflow processes → Response returned directly
```

No polling. The caller blocks until the workflow handler returns.

The client goes one step further and fuses the start and the update into a single
**Update-With-Start** RPC, so a brand-new session and its first turn are one atomic call.

### The Full Message Flow

Here is the complete path a message takes from an external caller to the LLM and back:

```
┌──────────────────────────┐
│   External Caller        │  var response = await proxy.RunAsync("Hello", session);
│   (TemporalAIAgentProxy) │
└───────────┬──────────────┘
            │
            │  1. Builds RunRequest { Messages, CorrelationId, EnableToolCalls, ... }
            │  2. Calls ITemporalAgentClient.SendAsync(sessionId, request)
            ↓
┌──────────────────────────────────────────────────────────────┐
│   DefaultTemporalAgentClient.SendAsync                       │
│                                                              │
│   3. startOp = WithStartWorkflowOperation.Create(            │
│        (AgentWorkflow wf) => wf.RunAsync(agentWorkflowInput),│
│        new WorkflowOptions(sessionId.WorkflowId, taskQueue)  │
│        {                                                     │
│          IdConflictPolicy = UseExisting,                     │
│          IdReusePolicy    = AllowDuplicate,                  │
│        })                                                    │
│                                                              │
│   4. await client.ExecuteUpdateWithStartWorkflowAsync<       │
│          AgentWorkflow, AgentResponse>(                      │
│        wf => wf.RunAgentAsync(request),                      │
│        new WorkflowUpdateWithStartOptions(startOp))          │
│                                                              │
│      ← ONE atomic RPC: starts-if-absent AND delivers the     │
│        update. Targets by workflow ID, so it follows the      │
│        continue-as-new chain. Blocks until the handler        │
│        returns.                                              │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow.RunAgentAsync                                │
│   [WorkflowUpdate("Run")]      ← wire name is "Run"          │
│                                                              │
│   5. await WaitForAgentInputAsync()                          │
│      ← deterministic readiness barrier. Update-With-Start     │
│        can admit this handler before the run method has       │
│        assigned _input. Completes synchronously for an        │
│        established session; schedules no timer or activity.   │
│                                                              │
│   6. requestEntry =                                          │
│        AgentSessionRequest.FromRunRequest(request, ...)      │
│   7. await base.RunTurnAsync(requestEntry, chatOptions: null)│
│        Inside the inherited base helper:                     │
│          await WaitConditionAsync(() => !_isProcessing)      │  ← Serialize
│          _isProcessing = true                                │
│          snapshot _history.Count, _turnCount, StateBag       │  ← Rollback point
│          _history.Add(requestEntry)                          │  ← Record request
│          _turnCount++                                        │
│          output = await ExecuteTurnAsync(...) ───────────────┼─┐
│                                                              │ │
└──────────────────────────────────────────────────────────────┘ │
                                                                 │
            (subclass override, in AgentWorkflow)                │
            ↓ ───────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow.ExecuteTurnAsync (override)                  │
│                                                              │
│   8. accumulated = FlattenHistoryMessages()                  │
│      (List<ChatMessage> seeded from history, which already    │
│       contains this turn's request entry)                    │
│                                                              │
│   9. Drive the durable loop:                                 │
│      for (iteration = 0; iteration < MaxToolCallsPerTurn; ++):│
│        stepInput = new AgentStepInput {                      │
│          AgentName, Request = runRequest,                    │
│          AccumulatedMessages = accumulated,                  │
│          SerializedStateBag = bagForStep,                    │
│          NeedsWorkerSettingsResolution = needsResolution }   │
│                                                              │
│        stepResult = await Workflow.ExecuteActivityAsync(     │
│          (AgentActivities a) =>                              │
│             a.RunDurableAgentStepAsync(stepInput))           │
│                                                              │
│        _currentStateBag = StateBagMerge.OverlayTrustedStateBag│
│          (_currentStateBag, stepResult.UpdatedStateBag)      │
│        accumulated.Add(stepResult.AssistantMessage)          │
│                                                              │
│        if (stepResult.IsFinal || no tool calls)              │
│          → return AgentResponse                              │
│                                                              │
│        // Policy-check each call against the frozen           │
│        // registered names; blocked calls get a synthetic     │
│        // result and schedule nothing.                        │
│        // Then (when an interceptor is configured) fan out     │
│        // RunToolInterceptor, then fan out InvokeAgentTool.    │
│        toolResults = await Workflow.WhenAllAsync(            │
│          dispatched.Select(d =>                              │
│            Workflow.ExecuteActivityAsync(                    │
│              (AgentActivities a) =>                          │
│                a.InvokeAgentToolAsync(d.Input),              │
│              d.Options)))                                    │
│                                                              │
│        accumulated.Add(new ChatMessage(ChatRole.Tool,        │
│          functionResultContents))                            │
│      // loop back                                            │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentActivities.RunDurableAgentStepAsync  [Activity]       │
│   "TemporalCommunity.Extensions.Agents.RunDurableAgentStep"  │
│                                                              │
│   10. blueprint = ResolveBlueprint(input.AgentName)          │
│       → AgentBlueprint (built lazily on FIRST dispatch,      │
│         then cached for the worker's lifetime). Holds ONLY   │
│         frozen shape:                                        │
│           ├─ Tools           (AIFunction registry, from root)│
│           ├─ Registration    (builder snapshot)              │
│           └─ AgentsOptions   (shared options snapshot)       │
│       It caches NO chat client, NO context providers, NO     │
│       interceptor, NO middleware and NO ChatClientAgent.     │
│                                                              │
│   11. using scope = serviceScopeFactory.CreateScope()        │
│       Resolved FRESH from that scope, every attempt:         │
│           ├─ chatClient      = registration.ChatClient(sp)   │
│           ├─ contextProviders = per-slot factories(sp)       │
│           ├─ toolInterceptor  = interceptor factory(sp)      │
│           └─ AIAgent pipeline = BuildLiveAgentPipeline(...)  │
│              (ChatClientAgent + user middleware, leased      │
│               and disposed before the scope)                 │
│                                                              │
│   12. sessionId = input.SessionId ?? Parse(ctx.Info.WorkflowId)│
│   13. session = TemporalAgentSession.FromStateBag(           │
│         sessionId, input.SerializedStateBag)                 │
│   14. Clone registration.ChatOptions; stamp Instructions,    │
│       the filtered durable Tools, and ResponseFormat         │
│   15. Run the AIContextProvider.InvokingAsync chain, folding │
│       each provider's aggregate into the next                │
│   16. Set TemporalAgentContext.Current (for tools)           │
│                                                              │
│   17. agent.RunStreamingAsync(augmentedMessages, session,    │
│         new ChatClientAgentRunOptions { ChatOptions })       │
│       ← goes through the AGENT, not the chat client, so any  │
│         DelegatingAIAgent the user installed via             │
│         ConfigureAgentPipeline fires around the call.        │
│       ← UseProvidedChatClientAsIs = true, so the model       │
│         returns FunctionCallContent instead of auto-invoking.│
│                                                              │
│   18. foreach update: collected.Add(update);                 │
│         ctx.Heartbeat(update.Text)                           │  ← Heartbeat (always)
│       response = collected.ToAgentResponse()                 │
│                                                              │
│   19. Run AIContextProvider.InvokedAsync (faults contained)  │
│                                                              │
│   20. Return AgentStepResult {                               │
│         IsFinal, AssistantMessage, ToolCalls,                │
│         UpdatedStateBag, Usage, ResponseId,                  │
│         ResolvedWorkerConfig? }                              │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow.ExecuteTurnAsync (after loop completes)      │
│                                                              │
│   21. return AgentResponse {                                 │
│         Messages  = allTurnMessages,                         │
│         Usage     = totalUsage,                              │
│         CreatedAt = Workflow.UtcNow }                        │
│                                                              │
│   Back in the base's RunTurnAsync:                           │
│   22. responseEntry =                                        │
│        BuildResponseEntry(corrId, output, Workflow.UtcNow)   │
│        └─ Subclass override returns                          │
│            AgentSessionResponse.FromAgentResponse(...)       │
│   23. _history.Add(responseEntry)                            │  ← Record response
│   24. _isProcessing = false                                  │  ← Release gate
│   25. return output                                          │  ← Update returns
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────┐
│   External Caller        │  response.Text == "Hello! How can I help?"
└──────────────────────────┘
```

### Fire-and-Forget Path

For cases where the caller does not need the response:

```csharp
await proxy.RunAsync("Do this in the background", session,
    new TemporalAgentRunOptions { IsFireAndForget = true });
```

This uses the `RunFireAndForget` signal instead of the `Run` update. For a brand-new session the
client fuses start and signal into one **Signal-With-Start** RPC, the same way `SendAsync` fuses
start and update:

```
Client → Signal-With-Start(RunFireAndForget) → Workflow receives signal
                                             → Kicks off ProcessFireAndForgetAsync as detached task
                                             → Returns immediately (no response to caller)
```

The signal handler starts a detached task inside the workflow that crosses the same input-readiness
barrier and then calls the same `RunTurnAsync` helper (serialize via `_isProcessing`, dispatch the
durable loop, record history) with no return value. Because there is no caller to surface a failure
to, `ProcessFireAndForgetAsync` logs and **swallows** any exception so one failed background turn
cannot kill the session. The base's turn rollback still applies, so a failed fire-and-forget turn
leaves no partial entry in history.

Before the run ends — on TTL expiry or at continue-as-new — the base awaits
`Workflow.AllHandlersFinished`, so a detached fire-and-forget turn is not aborted mid-flight.

---

## Durable Agent Composition

### Blueprint construction and per-step composition

The durable-agent dispatch path does not accept or cache a caller-built `AIAgent`. Instead
`AgentActivities` keeps a single `ConcurrentDictionary<string, AgentBlueprint>` cache, and the
blueprint is the *only* thing that is cached across activity calls.

**What the blueprint holds** — `AgentBlueprint` is an immutable record with exactly four members:

| Member | Source |
|---|---|
| `Tools` | `IReadOnlyDictionary<string, AIFunction>`, case-insensitive, resolved from the **root** provider |
| `ToolsAsAITools` | the same tools pre-cast to `IReadOnlyList<AITool>` |
| `Registration` | the `DurableAgentRegistration` snapshot produced by `DurableAgentBuilder` |
| `AgentsOptions` | a reference to the shared `TemporalAgentsOptions` snapshot |

**What the blueprint does *not* hold**: no `IChatClient`, no `AIContextProvider`, no tool
interceptor, no `ChatClientAgent`, and no middleware chain. Tool factories are the only thing
resolved from the root provider — `AIFunction` is a stateless delegate wrapper, so caching it
cannot create a captive scoped dependency.

**When it is built**: lazily, on the first activity dispatch that names the agent, via
`ResolveBlueprint`. All three activity entry points that need it (`RunDurableAgentStep`,
`RunToolInterceptor`, `InvokeAgentTool`) go through the same `GetOrAdd`, so whichever fires first
pays the build cost and the rest hit the cache for the worker's lifetime. Worker startup does
**not** pre-build blueprints.

**Four distinct lifetimes** coexist in this path — worth naming explicitly, because they are easy
to conflate:

| Lifetime | What | Where |
|---|---|---|
| Once per worker (lazy) | the blueprint: tool registry + registration + options | `ResolveBlueprint` |
| Once per activity attempt | chat client, context providers, tool interceptor, `ChatClientAgent`, middleware chain | a fresh `IServiceScope` inside each activity |
| Fresh per call, root-resolved, never cached | the keyed history reducer delegate | `ReduceHistoryByKey` activity |
| Once per registered pipeline at startup, **conditionally** | a dry-run middleware build for validation only | `DurableAgentPipelineValidator` |

For every LLM-step activity attempt, `RunDurableAgentStepAsync`:

1. Creates an activity DI scope and resolves `IChatClient` through `registration.ChatClient`.
2. Resolves each `AIContextProvider` through its factory from that scope, restores the
   `TemporalAgentSession` (history and StateBag) from the step input, and invokes the providers
   explicitly before and after the model call.
3. Clones `registration.ChatOptions`, stamps `Instructions`, the filtered durable tools, and
   `ResponseFormat`, and passes that complete effective value through `ChatClientAgentRunOptions`.
   The fresh `ChatClientAgent` is built with `ChatOptions = null`, `AIContextProviders = null`, and
   `UseProvidedChatClientAsIs = true`, so MAF cannot merge a second default tool/options set into
   the request.
4. Builds one configured `AIAgent` decorator chain from the same activity scope, walks it to reject
   function-invocation middleware, then disposes its owned wrapper (`OpenTelemetryAgent`) via the
   pipeline lease before the scope is disposed.

Because every one of those is per-attempt, **a retry rebuilds all of it**. That is the point: a
scoped dependency (a `DbContext`, a request-scoped credential) is never captured as an implicit
captive singleton. The cost is that the chain is constructed on every attempt, so middleware
constructors must be cheap and side-effect-free.

Failures in step 4 are classified: a `DurableConfigurationException` raised while composing the
pipeline is rethrown as a **non-retryable** `ApplicationFailureException`, because no number of
retries fixes a misconfiguration. The user's `registration.ChatClient(...)` call in step 1 is
deliberately *outside* that catch — it is user code that may fail transiently and keeps its normal
retry behaviour.

**Startup validation is narrower than it looks.** `DurableAgentPipelineValidator` runs at
`IPostConfigureOptions<TemporalWorkerServiceOptions>` time and builds no blueprint at all. It skips
an agent entirely unless that agent has a `ConfigureAgentPipeline` (or the worker sets
`DefaultConfigureAgentPipeline`), and the whole validator is a no-op when
`TemporalAgentsOptions.SkipDryRunCCheck` is set. When it does run, it composes the user's middleware
around `NoOpAgent.Instance` — not around a real `ChatClientAgent` over a real chat client — purely
to reject function-invocation middleware before the first conversation. So: an agent with no
configured pipeline has its middleware built for the first time inside its first activity attempt,
never at startup.

The explicit provider loop is intentional: it makes StateBag serialization and the durable-tool boundary visible to `AgentActivities`, rather than allowing MAF's internal agent loop to own them.

`UseProvidedChatClientAsIs = true` is load-bearing. Without it, MAF would auto-wrap the chat client in `FunctionInvokingChatClient`, which would execute tools inside the `IChatClient` pipeline — defeating the whole point of the design where the **workflow** owns the tool-dispatch loop and each tool call becomes its own `InvokeAgentTool` activity.

### Per-step `ChatOptions` shaping

Per-turn tool filtering and response format are applied inside `RunDurableAgentStepAsync` itself, **not** through a `DelegatingAIAgent` wrapper. The activity clones `registration.ChatOptions` per step and rewrites three fields based on the originating `RunRequest`:

```csharp
// Inside RunDurableAgentStepAsync:
var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
chatOptions.Instructions = registration.Instructions;
// Spread [..] makes a per-call copy so the downstream filter cannot corrupt the
// cached IReadOnlyList on the blueprint.
var selectedTools = AgentRunToolSelectionPolicy.FilterProviderTools(
    blueprint.ToolsAsAITools,
    input.Request.EnableToolCalls,
    input.Request.EnableToolNames is { } enabledToolNames ? [.. enabledToolNames] : null);
chatOptions.Tools = selectedTools.Count > 0 ? [.. selectedTools] : null;
chatOptions.ResponseFormat = input.Request.ResponseFormat;
```

Note `chatOptions.Tools` is set to `null` — not an empty list — when nothing is selected, and that
`chatOptions.Instructions` is only the *seed*: when context providers are registered, the
aggregated instructions they return overwrite this value after the provider chain runs. MAF's
`InvokingCoreAsync` concatenates each provider's contribution onto the input with `"\n"`, so the
effective value is `"registered\nprovider-1\nprovider-2"` — a provider appends through the default
path, it cannot replace.

`TemporalAgentRunOptions.EnableToolCalls` and `EnableToolNames` are copied onto `RunRequest` by `TemporalAIAgentProxy` / `TemporalAIAgent` before dispatch. Session workflows freeze them on `AgentSessionRequest`, so they survive Temporal serialization and reconstruction; job and containing-workflow paths carry the same `RunRequest` directly. `EnableToolCalls = false` exposes no tools. `EnableToolNames = null` exposes all registered tools, an empty list exposes none, and a non-empty list exposes only case-insensitive registered matches.

MAF durable tool names remain case-insensitive throughout selection and policy application.
`RequireApproval`, `SkipInterceptor`, interceptor timeout, and per-tool activity retry/timeout
lookups use ordinal case-insensitive matching explicitly after Temporal deserialization; they do
not rely on a dictionary comparer surviving the JSON boundary.

Provider filtering is not the security boundary. A model can still return a malformed, unknown, or previously visible function name. Immediately before interceptor fan-out, each of the three workflow loops (`AgentWorkflow`, `AgentJobWorkflow`, and `TemporalAIAgent`) applies the same deterministic policy against the frozen registered names. A blocked call schedules no interceptor, approval, or tool activity and receives the same tenant-visible synthetic result whether its name is unknown or merely excluded.

### Discovering session context from inside a tool

Tools dispatched in `InvokeAgentToolAsync` need to discover their workflow context — to call `TemporalAgentContext.Current.RequestApprovalAsync(...)`, to read the session ID, etc. Two mechanisms cover this:

- `TemporalAgentContext.Current` (an `AsyncLocal`) is set by `RunDurableAgentStepAsync` before the
  LLM call and by `InvokeAgentToolAsync` before each tool call. It exposes `CurrentSession` (the
  live `TemporalAgentSession`), `GetService` / `GetService<T>` over the activity's scoped
  `IServiceProvider`, `RequestApprovalAsync`, and the workflow-signalling helpers
  (`StartWorkflowAsync`, `SignalWorkflowAsync`, `GetWorkflowDescriptionAsync`).
- `TemporalAgentSession.GetService(typeof(TemporalAgentSessionId))` returns the session ID directly.
  `TemporalAgentContext.Current.CurrentSession` and any outer middleware see the *same* restored
  `TemporalAgentSession` instance the activity rebuilt from `AgentStepInput.SerializedStateBag`;
  middleware may make retry-safe StateBag changes on it but cannot replace it.

There is no `AgentWorkflowWrapper` interposed between the `ChatClientAgent` and the user's `IChatClient`. Application code that needs to decorate the `IChatClient` should do so by returning a decorated client from `agent.ChatClient` — see [`docs/how-to/MAF/llm-call-interception.md`](../../how-to/MAF/llm-call-interception.md).

---

## Durable Agent Workflow Loop

Every agent registered via `TemporalAgentsOptions.AddDurableAgent(...)` runs in **durable mode**: the agentic loop lives inside `[Workflow]` code, each LLM call is its own `RunDurableAgentStep` activity, and each tool call is its own `InvokeAgentTool` activity dispatched in parallel via `Workflow.WhenAllAsync`. There is no opt-in flag — this is the only worker-hosted agent-definition path; client-only processes declare proxies separately.

### Why the loop must live in the workflow

Temporal has a hard constraint: **activities cannot schedule child activities**. Only a workflow can call `Workflow.ExecuteActivityAsync`. There is no in-activity API for "run this thing as another activity and wait." The `TemporalCommunity.Extensions.AI` managed-session pattern works exactly because the workflow (`DurableChatWorkflow`, over the shared `DurableChatWorkflowBase<TOutput>`) owns the dispatch — the activity that drives the LLM call cannot fan out to per-tool activities of its own.

This eliminates several otherwise-tempting designs:

- **Channel-handoff inside the activity**: the activity blocks on a channel, sends tool-call requests "out," receives results. There is no Temporal coroutine primitive that supports this.
- **Faking `Workflow.InWorkflow = true` inside the activity**: would require modifying SDK internals and break workflow determinism guarantees.

The only implementable design within Temporal's public API is to put the dispatch loop in `[Workflow]` code. `AgentWorkflow.ExecuteDurableAgentTurnAsync` is that loop.

### Durable mode data flow

```
┌──────────────── DURABLE AGENT MODE (AddDurableAgent) ──────────────────────┐
│                                                                            │
│   AgentWorkflow.ExecuteDurableAgentTurnAsync                               │
│                                                                            │
│   accumulated = FlattenHistoryMessages()                                   │
│                                                                            │
│   for (iteration = 0; iteration < MaxToolCallsPerTurn; ++iteration):       │
│                                                                            │
│     ① Workflow.ExecuteActivityAsync(                                       │
│          (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput))     │
│                                                                            │
│           Inside the activity:                                             │
│           ├─ ResolveBlueprint(name) → AgentBlueprint (lazy, cached)        │
│           │    Tools · Registration · AgentsOptions  — nothing live        │
│           ├─ NEW DI scope → chat client, context providers, interceptor,   │
│           │    ChatClientAgent { UseProvidedChatClientAsIs = true },       │
│           │    user middleware chain      ← all per attempt                │
│           ├─ Clone registration.ChatOptions; stamp Instructions /          │
│           │    filtered Tools / ResponseFormat                             │
│           ├─ Run AIContextProvider.InvokingAsync chain                     │
│           ├─ agent.RunStreamingAsync(messages, session, runOptions)        │
│           │    ← through the AGENT, not the chat client                    │
│           ├─ Heartbeat per update                                          │
│           ├─ Run AIContextProvider.InvokedAsync chain                      │
│           └─ Return AssistantMessage + FunctionCallContent[] + StateBag    │
│                                                                            │
│     Overlay stepResult.UpdatedStateBag onto _currentStateBag (trusted)     │
│     accumulated.Add(stepResult.AssistantMessage)                           │
│                                                                            │
│     if (stepResult.IsFinal || no tool calls) → return AgentResponse        │
│                                                                            │
│     ② Deterministic policy check per call against the FROZEN registered    │
│        names. A blocked call schedules no interceptor, no approval and no  │
│        tool activity, and gets a synthetic result.                         │
│                                                                            │
│     ③ (only when an interceptor is configured)                             │
│        Workflow.WhenAllAsync(                                              │
│          Workflow.ExecuteActivityAsync(                                    │
│            (AgentActivities a) => a.RunToolInterceptorAsync(...)))         │
│        → Proceed / PauseForApproval / Skip / Block per call                │
│        Approval waits are all resolved BEFORE any tool activity starts.    │
│                                                                            │
│     ④ Workflow.WhenAllAsync(                                               │
│          dispatched.Select(d =>                                            │
│            Workflow.ExecuteActivityAsync(                                  │
│              (AgentActivities a) =>                                        │
│                a.InvokeAgentToolAsync(new InvokeAgentToolInput {           │
│                  AgentName, ToolName = tc.Name,                            │
│                  Arguments = tc.Arguments, CallId = tc.CallId,             │
│                  SerializedStateBag = bag }),                              │
│              ResolveDurableToolActivityOptions(tc.Name))))                 │
│                                                                            │
│     ⑤ Merge tool StateBag write-backs in TOOL-CALL INDEX order             │
│        (later index wins; reserved approval-scope deny-list applied)       │
│        accumulated.Add(toolResultMessage);  // FunctionResultContent items │
│        // loop back to ①                                                   │
│                                                                            │
│   Loop exhausted → append a synthetic assistant "iterations exceeded"      │
│   message and return the assembled AgentResponse (no throw).               │
│                                                                            │
└────────────────────────────────────────────────────────────────────────────┘
```

Note ⑤: the merge is ordered by **tool-call index**, never by activity completion order. Two tools
writing the same StateBag key must produce the same winner on replay, and completion order is not
replay-stable.

The mental model: **the workflow owns the tool-dispatch loop.** The activity runs the model call
through `agent.RunStreamingAsync` with `UseProvidedChatClientAsIs = true` and
`ChatOptions.Tools` populated from the agent's per-agent registry. The model returns
`FunctionCallContent` items; nothing executes them inside the activity. The workflow reads those
items back, policy-checks them, fans out one `InvokeAgentTool` activity per surviving call, awaits
them via `Workflow.WhenAllAsync`, builds `FunctionResultContent` messages from the results, and
loops.

### The activities on `AgentActivities`

`AgentActivities` declares **four** activities. Three participate in a durable turn; the fourth
fires only at continue-as-new.

| Activity name | Role |
|---|---|
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | One LLM call, through `agent.RunStreamingAsync`. Returns either a final assistant message or `FunctionCallContent[]`, plus the updated StateBag and usage. Runs the `AIContextProvider` chain explicitly around the call. On a proxy-started session's first step it also returns the resolved worker settings. |
| `TemporalCommunity.Extensions.Agents.RunToolInterceptor` | One pre-tool decision (`Proceed` / `PauseForApproval` / `Skip` / `Block`). Dispatched only when an interceptor is configured and the tool did not opt out. Fails **closed** for `ScopeAware + RequiresApproval` tools when no interceptor resolves — a deliberate asymmetry with MEAI's fail-open equivalent. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | One tool dispatch. Resolves the tool from the blueprint's per-agent registry — names do not collide across agents. Throws when the name is unregistered. |
| `TemporalCommunity.Extensions.Agents.ReduceHistoryByKey` | Applies the DI-resolved keyed history reducer at continue-as-new. Not part of a turn. |

The split is intentional: `InvokeAgentTool` carries the `AgentName` so the blueprint can resolve the tool against its per-agent registry. Two agents on the same worker can register tools with the same `AIFunction.Name` without collision, and the Temporal Web UI shows each activity as a distinct row so operators can read the dispatch shape at a glance.

### Why `Workflow.WhenAllAsync` and not `Task.WhenAll`

The fan-out step uses `Workflow.WhenAllAsync` — the Temporal SDK's workflow-safe combinator — not `Task.WhenAll`:

```csharp
var toolOutputs = await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);
```

Inside a Temporal `[Workflow]`, `await`s must run on the workflow scheduler so that task continuations are deterministic on replay. `Task.WhenAll` is technically safe in many cases (when all the awaited tasks come from `Workflow.ExecuteActivityAsync`, which schedules continuations on `TaskScheduler.Current`), but `Workflow.WhenAllAsync` is the project convention and is documented as "the workflow-safe equivalent of `Task.WhenAll`" by the SDK itself. `WorkflowAgents.ExecuteAgentsInParallelAsync` (`src/TemporalCommunity.Extensions.Agents/WorkflowAgents.cs:112`) already uses it for the parallel-agent pattern; the durable loop follows the same convention.

`Workflow.WhenAllAsync` preserves input order. The result array is index-aligned with the input
task list, which is what makes the ordered StateBag merge possible.

One subtlety: the result array is **not** index-aligned with `toolCalls`, because a blocked or
skipped call schedules no activity at all. The workflow builds the awaited list from the
non-null slots of a `Task<InvokeAgentToolResult>?[]` sized to `toolCalls.Count`, then walks
`toolCalls` with a separate `pendingIdx` cursor into the results, substituting the synthetic
result for any index that was never dispatched. `FunctionResultContent` is always constructed with
`callId: toolCalls[i].CallId`, so correlation back to the model's request is by call ID, not by
position in the result array.

### The iteration cap as a workflow-history bound

Temporal's per-workflow event-history limit (~50K events) is a hard constraint on any in-workflow loop. Each iteration in the durable loop contributes:

- One `ActivityScheduled` + one `ActivityCompleted` (or `ActivityFailed` + retries) for the `RunDurableAgentStep` call
- One pair per tool call in the fan-out batch
- **Plus** one pair per tool call for `RunToolInterceptor`, when an interceptor is configured

Counting a turn with `K` tool calls per round that converges after `N` tool rounds: `N + 1`
`RunDurableAgentStep` activities and `N × K` `InvokeAgentTool` activities — so `N + 1 + N·K` in
total, doubled in event count, and increased by a further `N × K` activities when an interceptor is
configured. The often-quoted `2N + 1` figure is only the `K = 1`, no-interceptor case.

Worked example: a single round that issues two tool calls and then converges costs 2 LLM-step
activities + 2 tool activities = 4 activities, so **8 events minimum** — 12 with an interceptor.
Add retries and each failed attempt adds its own `ActivityTaskFailed`/`ActivityTaskStarted` pair.

A model that loops indefinitely on tool calls would consume the history budget and force a
continue-as-new mid-turn, which is harder to reason about than a clean structured failure.

`MaxToolCallsPerTurn` (default `20`, configured per-agent on `DurableAgentBuilder`; there is no
worker-level fallback, and the builder rejects a non-positive value) bounds the loop counter. When the cap is exceeded, the workflow does not throw; it appends an `assistant` message of the form

```
Maximum tool-call iterations (N) exceeded for agent 'AgentName'. The agent did not converge on a final answer.
```

to the transcript and returns the assembled `AgentResponse`. From the caller's perspective the turn completes successfully with a response that calling code can detect and handle.

### Determinism rules

The same workflow-determinism rules apply inside `ExecuteDurableAgentTurnAsync` as anywhere else in `[Workflow]` code. Cross-reference the [Do's and Don'ts — Workflow Determinism](../../how-to/MAF/dos-and-donts.md#workflow-determinism) table:

| Concern | Rule | Why |
|---|---|---|
| Parallel fan-out | `Workflow.WhenAllAsync(tasks)` | Project convention; the SDK-provided workflow-safe combinator |
| Wall-clock time | `Workflow.UtcNow` | `DateTime.UtcNow` differs across replay |
| Random GUIDs | `Workflow.NewGuid()` | `Guid.NewGuid()` differs across replay |
| Logging | `Workflow.Logger` | Direct `ILogger` captured via closure misbehaves on replay |
| OTel spans | None inside the loop | `ActivitySource.StartActivity()` is non-deterministic; `agent.turn` lives inside `RunDurableAgentStepAsync` instead |
| `await` continuations | `.ConfigureAwait(true)` (or omit `ConfigureAwait` entirely) | `ConfigureAwait(false)` opts out of `TaskScheduler.Current`, so later workflow commands no longer execute through the active workflow context |
| Threading | No `Task.Run`, no threads, no `Task.Delay`, no `Thread.Sleep` | Same scheduler-stripping risk |
| Hashing | A fixed-seed hash (the loop uses FNV-1a 32-bit), never `string.GetHashCode()` | .NET randomizes `GetHashCode` per process, so two runs would disagree and diverge on replay |
| Changing loop logic in a shipped workflow | `Workflow.Patched(id)` / `Workflow.DeprecatePatch(id)` | The .NET SDK's versioning primitives. `Workflow.GetVersion` is the Go/Java API and does not exist here. |

The durable loop in `AgentWorkflow.cs` follows all of these rules: every `await` either omits `ConfigureAwait` or uses `ConfigureAwait(true)`; the loop counter is a local `int`; iteration timestamps for the final `AgentResponse.CreatedAt` come from `Workflow.UtcNow`; and there is no in-workflow OTel span (the `agent.turn` span fires inside `RunDurableAgentStepAsync`, which runs in activity context).

The StateBag dispatch optimization is a concrete example of the hashing rule. The workflow passes
the full serialized bag on the first step of each turn and, on later steps, passes `null` when a
stable content hash shows the bag is unchanged — avoiding tens of kilobytes of redundant history
bytes per iteration. That hash is a workflow-thread local that never enters Temporal history, and it
is reset at every continue-as-new and whenever a turn rollback discards the bag. Activities
receiving `null` must behave identically to receiving the unchanged bag; `_currentStateBag` in the
workflow is always the authority.

### Continue-as-new across durable mode

`AgentWorkflowInput.DurableAgentToolActivityOptions` is not a stored field — it forwards to
`ResolvedWorkerConfig.ToolActivityOptions`. That bundle is frozen at one of two points, depending
on how the session started:

- **Worker-started session** (this process holds the `AddDurableAgent` registration):
  `DefaultTemporalAgentClient` builds `ResolvedWorkerConfig` at workflow start, from the agent's
  per-tool `DurableToolOptions`.
- **Proxy-started session** (this process only declared `AddAgentProxy`, so it has no registration):
  `ResolvedWorkerConfig` starts `null`. `WorkerSettingsResolved` is therefore `false`, and the
  workflow sets `NeedsWorkerSettingsResolution` on the **first step of the first turn**. The worker
  resolves the bundle from its own `DurableAgentRegistration`, returns it on `AgentStepResult`, and
  the workflow patches `_input` mid-loop. This is why `MaxToolCallsPerTurn` is re-read on every
  iteration rather than snapshotted before the loop.

Once resolved, the bundle is carried through `CreateContinueAsNewException` unchanged. A write tool
registered with `opts.NoRetry()` keeps `MaximumAttempts = 1` across every continue-as-new
boundary — the options dictionary is never re-read from the registration after resolution, so
registration-time changes do not bleed into running workflows. Continue-as-new is settings-frozen.

A tool with no entry in that dictionary falls back to activity options built from the workflow
input's `ActivityTimeout`, `HeartbeatTimeout`, and `RetryPolicy` — which for a write tool is the
wrong answer. Register write tools with an explicit `opts.NoRetry()`.

For the user-facing how-to, see [the durable-agents how-to](../../how-to/MAF/durable-agents.md).

---

## Crashes, Heartbeats, and Timeouts

### Architecture Summary for Resilience

```
┌──────────────────────────────────────────────────────────────────┐
│                        TEMPORAL SERVER                           │
│   Persists: workflow event history, timer state, task queues     │
└──────────────────────────┬───────────────────────────────────────┘
                           │
              ┌────────────┴────────────┐
              ↓                         ↓
   ┌──────────────────┐     ┌──────────────────┐
   │   Worker A        │     │   Worker B        │
   │   (running)       │     │   (standby)       │
   │                   │     │                   │
   │   AgentWorkflow   │     │   Can pick up     │
   │   AgentActivities │     │   any workflow    │
   └──────────────────┘     └──────────────────┘
```

The Temporal server is the single source of truth. Workers are stateless executors. Any worker can resume any workflow.

### Timeout Configuration

There are three timeouts that affect agent execution, all configurable via `TemporalAgentsOptions` or `AgentWorkflowInput`:

#### 1. Activity `StartToCloseTimeout` (default: 5 minutes)

```csharp
new ActivityOptions
{
    StartToCloseTimeout = _input.ActivityTimeout,
}
```

**What it controls**: Maximum wall-clock time for a single `RunDurableAgentStepAsync` (or `InvokeAgentToolAsync`) activity execution, measured from when the worker starts executing the activity to when it must return a result. It is a **per-attempt** limit, not a total budget for the call.

**What happens on timeout**: Temporal marks the attempt as failed and **retries it under the
effective retry policy**. The library never leaves `ActivityOptions.RetryPolicy` null: a null
policy is transmitted as "server default", which means `MaximumAttempts = 0` — *unlimited* retries —
so a permanently-failing LLM call would retry forever and hang the caller. Instead
`DefaultRetryPolicy` substitutes a bounded default when the user configured none:

| Workload | Default when unconfigured |
|---|---|
| Model step (`RunDurableAgentStep`) | `MaximumAttempts = 5`, `MaximumInterval = 2s` |
| Tool / policy activities | `MaximumAttempts = 5`, `MaximumInterval = 30s` |

Only when the attempt budget is exhausted does `ExecuteActivityAsync` throw. At that point the
base's `RunTurnAsync` rolls the whole turn back (request entry, turn count, StateBag) and the
Update surfaces the failure to the caller; the workflow itself stays alive for the next turn.

Set `opts.DefaultRetryPolicy` (or the per-agent `RetryPolicy`) to override. Non-idempotent tools
should use the per-tool `opts.NoRetry()` rather than widening the worker-level policy.

**When to increase**: If your LLM calls are slow (large context or complex tool chains).

**When to decrease**: If you want faster failure detection for stuck LLM calls.

```csharp
// Configure via options
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddHostedTemporalWorker("task-queue")
    .AddTemporalAgents(opts =>
    {
        opts.DefaultActivityTimeout = TimeSpan.FromMinutes(60);
        opts.AddDurableAgent("MyAgent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

#### 2. Activity `HeartbeatTimeout` (default: 2 minutes)

```csharp
new ActivityOptions
{
    HeartbeatTimeout = _input.HeartbeatTimeout,
}
```

**What it controls**: Maximum time between consecutive heartbeats. If the activity does not heartbeat within this window, Temporal considers the activity — and by extension, the worker — to be dead.

**How heartbeats are sent**:

The model-step activity consumes provider updates and sends a heartbeat for every update while it
builds the completed response:

```csharp
var collected = new List<AgentResponseUpdate>();
await foreach (var update in agent.RunStreamingAsync(
        augmentedMessages, session, runOptions, ct).WithCancellation(ct).ConfigureAwait(false))
{
    collected.Add(update);
    ctx.Heartbeat(update.Text);    // ← Heartbeat fired on every update
}
var response = collected.ToAgentResponse();
```

`InvokeAgentToolAsync` heartbeats once, on entry (`ctx.Heartbeat($"invoking tool '{name}'")`). A
long-running tool that never heartbeats again will trip `HeartbeatTimeout` — give such tools a
per-tool `HeartbeatTimeout` via `DurableToolOptions`, or heartbeat from inside the tool through
`ActivityExecutionContext.Current`.

**What happens on heartbeat timeout**: Temporal cancels the activity's `CancellationToken` and marks it as timed out. This is the primary mechanism for detecting a dead worker during long LLM calls. Caller-visible `RunStreamingAsync` is intentionally unsupported.

**Key insight**: `HeartbeatTimeout` is active while the model-step activity consumes provider updates. It is not a caller-visible streaming transport.

#### 3. Workflow `TimeToLive` (default: 14 days)

```csharp
// input.TimeToLive is a non-nullable TimeSpan whose default is already 14 days —
// there is no null-coalesce here.
bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested
          || (!_isProcessing && Workflow.ContinueAsNewSuggested)
          || (!_isProcessing && _history.Count >= input.MaxEntryCount),
    timeout: input.TimeToLive);
```

**What it controls**: How long the workflow stays alive waiting for new messages. This is not a Temporal-enforced timeout — it is the `timeout` parameter to `WaitConditionAsync`.

**What happens when TTL expires**: The wait returns `false`, the workflow waits for
`Workflow.AllHandlersFinished` so no detached fire-and-forget turn is aborted, and completes
normally. The session is done. Any subsequent message to this session ID will start a **new**
workflow (because `IdReusePolicy = AllowDuplicate`).

**When to adjust**: Set shorter TTLs for ephemeral sessions (chatbots, one-off queries). Set longer TTLs for persistent agents that should stay alive across days or weeks.

```csharp
opts.AddDurableAgent("MyAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.TimeToLive = TimeSpan.FromHours(1);
});
// or — worker-level default for every agent that does not override
opts.DefaultTimeToLive = TimeSpan.FromDays(7);
```

### Crash Scenarios

#### Scenario A: Worker Crashes During Activity (LLM Call In Progress)

```
AgentWorkflow → ExecuteActivityAsync → AgentActivities running → [WORKER DIES]
```

**Timeline:**

1. Activity is executing (LLM call in progress)
2. Worker process crashes (OOM, hardware failure, deployment)
3. Temporal detects the failure via one of:
   - **HeartbeatTimeout**: Because heartbeats are sent while the model-step activity consumes provider updates, Temporal notices when the window passes with no heartbeat
   - **Worker disconnect**: Temporal detects the worker's gRPC connection dropped
4. Temporal marks the activity **attempt** as failed
5. The workflow is still blocked on `ExecuteActivityAsync`, waiting for a result
6. A new worker picks up the workflow task from the task queue
7. The new worker **replays** the workflow from the beginning:
   - All prior completed activities return cached results from history
   - The failed attempt is **rescheduled** under the effective retry policy (bounded to five
     attempts when the user configured none)
8. The activity runs again on the new worker (fresh LLM call, and a fresh chat client, context
   providers, interceptor and middleware chain — everything except the cached blueprint is rebuilt)
9. If it succeeds, the result is recorded and the workflow continues. If the attempt budget is
   exhausted, the turn fails and is rolled back (below).

**Data loss**: None. The conversation history up to the failed turn is in `_history`
(reconstructed during replay from activity results in the event history). Activity retries happen
inside the same turn. If the turn ultimately fails, the base workflow rolls back the request entry
and turn count before the Update failure is returned. A later turn therefore cannot accidentally
send the failed request to the model as conversation history. `AgentWorkflow` also restores the
StateBag snapshot from before the turn, so application-, provider-, interceptor-, and tool-owned
mutations from that failed turn do not leak forward. Snapshot capture and restoration occur inside
the base workflow's serialized-turn gate. A queued Update therefore snapshots only after the
preceding turn commits, and rollback cannot replace that committed StateBag with state observed
before the queued Update entered the gate. Reserved approval-scope records are retained: their
decisions were committed through independent approval updates while the turn was parked.

#### Scenario B: Worker Crashes Between Activities (Workflow Code Running)

```
AgentWorkflow: Activity1 ✓ → Activity2 ✓ → [doing workflow logic] → [WORKER DIES]
```

**Timeline:**

1. Activities 1 and 2 completed and their results are in the event history
2. Worker crashes while running workflow code between activity calls
3. New worker picks up the workflow task
4. Replays from the beginning:
   - `ExecuteActivityAsync(Activity1)` → returns cached result (**not re-executed**)
   - `ExecuteActivityAsync(Activity2)` → returns cached result (**not re-executed**)
   - Workflow code continues from where it left off

**Data loss**: None.

#### Scenario C: Worker Crashes During WorkflowUpdate Handler

```
Client waiting on the Update result → AgentWorkflow.RunAgentAsync running → [WORKER DIES]
```

**Timeline:**

1. Client is blocking on `ExecuteUpdateWithStartWorkflowAsync(wf => wf.RunAgentAsync(request), ...)`
2. Worker crashes mid-update
3. New worker picks up the workflow, replays, and the update handler re-executes
4. Once the update completes on the new worker, the response is delivered to the waiting client

**Client experience**: The update call blocks until the handler completes (even across worker failures). The client does not need retry logic — Temporal handles the handoff transparently.

**Important caveat**: If the client's own connection to Temporal drops during the wait, the client will need to re-send the update. Since `_isProcessing` serializes updates, this is safe — a duplicate update will simply queue behind the in-progress one.

#### Scenario D: Temporal Server Restarts

If the Temporal server itself restarts:

1. All workflow state is persisted in the server's database (Cassandra, PostgreSQL, MySQL, or SQLite for dev)
2. Workers reconnect automatically
3. Workflows resume from their persisted state
4. No data loss

### Heartbeat Detail: What Gets Sent

On every provider update, the update text is sent as the heartbeat detail:

```csharp
ctx.Heartbeat(update.Text);
```

This has two benefits:

1. **Liveness**: Temporal knows the activity is still alive
2. **Progress visibility**: The heartbeat detail is visible in the Temporal UI and via `DescribeWorkflowExecution`, so operators can see the LLM's partial output in real time

### Timeout Interaction Diagram

```
                    0 min          2 min          5 min        14 days
                    │              │              │              │
                    ├──────────────┤              │              │
                    │ Heartbeat    │              │              │
                    │ Timeout      │              │              │
                    │ (2 min)      │              │              │
                    │              │              │              │
                    ├──────────────┴──────────────┤              │
                    │ StartToClose Timeout         │              │
                    │ (5 min)                      │              │
                    │                              │              │
                    ├──────────────────────────────┴──────────────┤
                    │ Workflow TTL                                 │
                    │ (14 days)                                    │
Activity start ─────┘                                              └── Workflow ends

• HeartbeatTimeout: Dead-worker detection while the model-step activity consumes provider updates
• StartToCloseTimeout: Hard limit on any single agent turn
• Workflow TTL: How long the session stays alive between messages
```

### Summary Table

| Timeout | Default | Scope | Detection | Configurable Via |
|---------|---------|-------|-----------|------------------|
| `HeartbeatTimeout` | 2 min | Single activity | Worker death during model updates | `TemporalAgentsOptions.DefaultHeartbeatTimeout` |
| `StartToCloseTimeout` | 5 min | Single activity **attempt** | Stuck/slow LLM call | `TemporalAgentsOptions.DefaultActivityTimeout` |
| `RetryPolicy` | 5 attempts (2s max interval for model steps, 30s for tools) | All attempts of one activity | Permanently failing LLM or tool call | `TemporalAgentsOptions.DefaultRetryPolicy`, per-agent `RetryPolicy`, or per-tool `DurableToolOptions` |
| `ApprovalTimeout` | 7 days | One HITL approval wait | Reviewer never responds | `TemporalAgentsOptions.DefaultApprovalTimeout` |
| `TimeToLive` | 14 days | Entire workflow | Session inactivity | `TemporalAgentsOptions.DefaultTimeToLive` or per-agent |

| Crash Scenario | Data Loss | Recovery | Automatic? |
|---------------|-----------|----------|------------|
| Worker dies during activity | None | Attempt retried on new worker, under the bounded retry policy | Yes |
| Activity retry budget exhausted | Turn discarded, not partially recorded | Base rolls back the request entry, turn count and StateBag; Update surfaces the failure; session stays alive | Yes |
| Worker dies between activities | None | Workflow replayed, cached results returned | Yes |
| Worker dies during update | None | Update re-executes on new worker, client blocks until done | Yes |
| Temporal server restarts | None | Workers reconnect, workflows resume | Yes |
| Client disconnects during update | Possible duplicate request | Client re-sends update; serialized via `_isProcessing` | Manual |

---

## Related Documentation

- [durability-and-determinism.md](./durability-and-determinism.md) — Step-by-step walkthrough of deterministic replay with agent calls
- [session-statebag-and-context-providers.md](./session-statebag-and-context-providers.md) — StateBag semantics and the `AIContextProvider` contract
- [CLAUDE.md](../../../CLAUDE.md) — Project architecture overview and quick reference
