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

`TemporalAgentSession` extends the framework's `AgentSession` and wraps a `TemporalAgentSessionId`:

```csharp
public sealed class TemporalAgentSession : AgentSession
{
    public TemporalAgentSessionId SessionId { get; }

    // Service locator pattern — allows agents and tools to discover the session ID
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
            return this.SessionId;
        return base.GetService(serviceType, serviceKey);
    }
}
```

**Key behaviors:**

- **Serialization**: Serializes to/from JSON as `{ "sessionId": "ta-name-key", "stateBag": { ... } }`. The workflow ID string is the canonical representation.
- **Implicit conversions**: `TemporalAgentSessionId` converts implicitly to/from `string`, so it can be passed anywhere a workflow ID is expected.
- **ToString**: Returns the workflow ID, making it easy to log and debug.

### How Session Maps to Workflow

The mapping is 1:1:

```
TemporalAgentSession
    └─ SessionId: TemporalAgentSessionId
         └─ WorkflowId: "ta-weatherassistant-a1b2c3d4"
              └─ Maps to: AgentWorkflow instance with this workflow ID
```

When `DefaultTemporalAgentClient` receives a session ID, it calls `StartWorkflowAsync` with `IdConflictPolicy = UseExisting`. This means:
- **First call**: Creates a new `AgentWorkflow` with this workflow ID
- **Subsequent calls**: No-ops — the workflow already exists

The session effectively *is* the workflow. Creating a session doesn't start the workflow; the first `RunAsync` call does.

---

## The Agent Loop Inside AgentWorkflow

### Inheritance: shared session loop, MAF-specific overrides

`AgentWorkflow` inherits from `DurableChatWorkflowBase<AgentResponse>` (declared in
`TemporalCommunity.Extensions.AI`). The base class owns the session-loop body — the turn
mutex, continue-as-new triggering, history reduction, the `[WorkflowQuery("GetHistory")]`
handler, the `[WorkflowSignal("RequestShutdown")]` handler, and the generic HITL
approval methods (`RequestApproval`, `ResolveApproval`, and `GetPendingApproval`). MAF
inherits these and adds `ResolveAgentApproval` for decisions that include reusable scopes.

The shared shape:

```
DurableChatWorkflowBase<TOutput>           ← in TemporalCommunity.Extensions.AI
    ├─ _history: List<DurableSessionEntry> (private)
    ├─ session-loop body (turn mutex, CAN trigger, history reducer)
    ├─ [WorkflowQuery("GetHistory")]
    ├─ [WorkflowSignal("RequestShutdown")]
    ├─ [WorkflowUpdate("RequestApproval" | "ResolveApproval")] + validators
    ├─ [WorkflowQuery("GetPendingApproval")]
    ├─ protected abstract BuildResponseEntry(...)         ← MAF override below
    ├─ protected abstract ExecuteTurnAsync(...)           ← MAF override below
    ├─ protected abstract CreateContinueAsNewException(...) ← MAF override below
    └─ protected virtual UpsertCustomSearchAttributes()   ← MAF override below

AgentWorkflow : DurableChatWorkflowBase<AgentResponse>     ← in TemporalCommunity.Extensions.Agents
    ├─ _currentStateBag: JsonElement?  (MAF-specific)
    ├─ _input: AgentWorkflowInput?     (MAF-specific)
    ├─ [WorkflowUpdate("RunAgent")] + [WorkflowUpdateValidator]
    ├─ [WorkflowUpdate("ResolveAgentApproval")] for MAF scoped decisions
    ├─ [WorkflowSignal("RunFireAndForget")]
    ├─ override BuildResponseEntry → AgentSessionResponse.FromAgentResponse(...)
    ├─ override ExecuteTurnAsync   → drives ExecuteDurableAgentTurnAsync (the durable loop)
    ├─ override CreateContinueAsNewException → carries _currentStateBag forward
    └─ override UpsertCustomSearchAttributes → upserts AgentName
```

`AgentWorkflowInput` itself inherits from `DurableChatWorkflowInput`, so the
shared fields (`MaxEntryCount`, `HistoryReducerKey`, `EnableSearchAttributes`, etc.)
come from the base, while MAF-only fields (`AgentName`, `TaskQueue`,
`CarriedStateBag`, `RetryPolicy`) live on the subclass.

### Lifecycle Overview

`AgentWorkflow` is the durable backbone of every agent session. It is a long-lived Temporal workflow that:

1. **Starts** when the first message is sent to an agent session
2. **Waits** for incoming messages (via Update or Signal)
3. **Dispatches** each message to an activity that runs the real AI agent
4. **Accumulates** conversation history as workflow state
5. **Continues-as-new** when history grows too large
6. **Shuts down** when a Shutdown signal arrives or the TTL expires

Steps 2, 4, 5, and 6 are implemented by the base class. Step 3 is the
subclass's `ExecuteTurnAsync` override (which dispatches `AgentActivities`
rather than `DurableChatActivities`).

### The Main Run Loop

`AgentWorkflow.RunAsync` is a thin shim that wires up the MAF-specific
state and then delegates to the base:

```csharp
[WorkflowRun]
public Task RunAsync(AgentWorkflowInput input)
{
    _input = input;
    _currentStateBag = input.CarriedStateBag;   // MAF-only: restore StateBag
    return base.RunAsync(input);                // Base owns the loop
}
```

Inside the base, the loop looks like this (paraphrased — see
`DurableChatWorkflowBase<TOutput>` for the canonical implementation):

```csharp
// In DurableChatWorkflowBase<TOutput>.RunAsync(DurableChatWorkflowInput input):
_history.AddRange(input.CarriedHistory);            // Restore from CAN
_turnCount = InitializeTurnCount(input.CarriedHistory); // Re-derive from history

if (input.EnableSearchAttributes)
{
    Workflow.UpsertTypedSearchAttributes(/* TurnCount, SessionCreatedAt */);
    UpsertCustomSearchAttributes();                 // Subclass hook (MAF: AgentName)
}

TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);
bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
    timeout: ttl);

if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
    throw CreateContinueAsNewException(input);      // Subclass hook
```

This is **not** a tight polling loop. `WaitConditionAsync` is an event-driven primitive that parks the workflow until one of these conditions becomes true:

- `_shutdownRequested` — set by the `RequestShutdown` signal (handler is on the base)
- `Workflow.ContinueAsNewSuggested` — set by Temporal when history approaches size limits
- The TTL timeout elapses

While the workflow is parked, it is **not consuming compute resources**. It sits in Temporal's persistence layer and only wakes when a message (Update or Signal) arrives.

### The Processing Gate: `_isProcessing`

The base serializes concurrent requests with a boolean gate. The subclass's
`[WorkflowUpdate("RunAgent")]` handler delegates the actual turn execution
to the base's `RunTurnAsync` helper, which acquires the gate, appends the
request entry, calls `ExecuteTurnAsync` (the subclass override), appends
the response entry, and releases the gate:

```csharp
// In AgentWorkflow:
[WorkflowUpdate("RunAgent")]
public async Task<AgentResponse> RunAgentAsync(RunRequest request)
{
    Workflow.Logger.LogWorkflowUpdateReceived(_input!.AgentName, /* ... */);

    var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
    var (output, _) = await RunTurnAsync(requestEntry, chatOptions: null);

    Workflow.Logger.LogWorkflowUpdateCompleted(_input!.AgentName, /* ... */);
    return output;
}
```

`RunTurnAsync` (on the base) wraps the body in the mutex:

```csharp
// In DurableChatWorkflowBase<TOutput>.RunTurnAsync(...):
await Workflow.WaitConditionAsync(() => !_isProcessing);
_isProcessing = true;
try
{
    _history.Add(requestEntry);
    _turnCount++;

    var output = await ExecuteTurnAsync(activityOptions, requestEntry, chatOptions);
    var responseEntry = BuildResponseEntry(requestEntry.CorrelationId, output, Workflow.UtcNow);

    _history.Add(responseEntry);
    return (output, responseEntry);
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

### MAF-specific subclass hooks

The four overrides on `AgentWorkflow`:

| Hook | Purpose |
|---|---|
| `BuildResponseEntry(correlationId, AgentResponse output, createdAt)` | Returns `AgentSessionResponse.FromAgentResponse(...)` so the entry on the wire is the MAF subclass with `OrchestrationId`/`ResponseType`/`ResponseSchema` discrimination preserved. |
| `ExecuteTurnAsync(activityOptions, requestEntry, chatOptions)` | Drives the per-step durable loop in `ExecuteDurableAgentTurnAsync`: each iteration dispatches `AgentActivities.RunDurableAgentStepAsync` (one LLM call) and, if the step returns pending tool calls, fans out one `AgentActivities.InvokeAgentToolAsync` activity per call via `Workflow.WhenAllAsync`. Persists the updated StateBag back into `_currentStateBag` after each step. |
| `CreateContinueAsNewException(input)` | Casts `input` to `AgentWorkflowInput` (safe — `AgentWorkflowInput : DurableChatWorkflowInput`) and constructs a new `AgentWorkflowInput` carrying `_currentStateBag` forward as `CarriedStateBag` so the StateBag survives continue-as-new boundaries. |
| `UpsertCustomSearchAttributes()` | Upserts the `AgentName` typed search attribute. Called by the base after the standard `TurnCount` / `SessionCreatedAt` upserts. Default in the base is a no-op; `DurableChatWorkflow` (the MEAI sibling) does not override it because chat sessions are not named. |

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

When a turn starts, `ExecuteDurableAgentTurnAsync` flattens `_history` into a working `accumulated` list of `ChatMessage` objects. That list is the seed for the per-step loop: each `RunDurableAgentStep` activity receives it as `AgentStepInput.AccumulatedMessages`, the LLM sees the full conversation on every iteration, and the workflow appends each step's assistant message (and any tool-result message) back onto the list before the next iteration. Because `entry.Messages` is already `IReadOnlyList<ChatMessage>`, no conversion step is needed:

```csharp
// Inside AgentWorkflow.ExecuteDurableAgentTurnAsync, before the loop:
var accumulated = FlattenHistoryMessages();   // _history → flat List<ChatMessage>

// allMessages now contains: [User: "Hi", Assistant: "Hello!", User: "Weather?"]
// Each iteration re-sends this (plus any in-turn tool messages) to the LLM
```

### Continue-as-New: History Carryover

Temporal workflows have a practical limit on event history size (typically ~50K events). When the history grows large, `Workflow.ContinueAsNewSuggested` becomes true. The workflow then:

1. Snapshots `_history` into a list (base does this)
2. Calls `CreateContinueAsNewException(input)` (subclass override produces the typed exception)
3. The MAF override builds a fresh `AgentWorkflowInput` carrying `_history` as `CarriedHistory` **and** `_currentStateBag` as `CarriedStateBag`, then returns `Workflow.CreateContinueAsNewException<AgentWorkflow>(...)`
4. Temporal starts a **new run** of the same workflow ID
5. The new run restores `_history` from `input.CarriedHistory` (in the base) and `_currentStateBag` from `input.CarriedStateBag` (in `AgentWorkflow.RunAsync`)
6. The base's `InitializeTurnCount` re-derives `_turnCount` from the carried history (counting `DurableSessionResponse` entries), so the `TurnCount` search attribute monotonically grows across CAN boundaries

From the caller's perspective, nothing changes — the workflow ID is the same, and the conversation continues seamlessly. The StateBag carry-forward is the MAF-specific piece; everything else is shared with `DurableChatWorkflow`.

---

## Sending Messages via WorkflowUpdate

### Why Updates Instead of Signal + Query

The traditional Temporal pattern for request/response is:

```
Client → Signal(request) → Workflow processes → Client polls Query until result ready
```

This works but requires a polling loop on the client side. `WorkflowUpdate`, introduced in Temporal SDK 1.x, provides a synchronous alternative:

```
Client → Update(request) → Workflow processes → Response returned directly
```

No polling. The caller blocks until the workflow handler returns.

### The Full Message Flow

Here is the complete path a message takes from an external caller to the LLM and back:

```
┌──────────────────────────┐
│   External Caller        │  var response = await proxy.RunAsync("Hello", session);
│   (TemporalAIAgentProxy) │
└───────────┬──────────────┘
            │
            │  1. Builds RunRequest { Messages, CorrelationId, ... }
            │  2. Calls ITemporalAgentClient.SendAsync(sessionId, request)
            ↓
┌──────────────────────────────────────────┐
│   DefaultTemporalAgentClient             │
│                                          │
│   3. StartWorkflowAsync(AgentWorkflow)   │  ← Idempotent: creates or no-ops
│      IdConflictPolicy = UseExisting      │
│                                          │
│   4. GetWorkflowHandle(sessionId)        │  ← Unpinned: follows continue-as-new
│                                          │
│   5. handle.ExecuteUpdateAsync(          │  ← Blocks until handler returns
│        wf => wf.RunAgentAsync(request))  │
└───────────┬──────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow.RunAgentAsync                                │
│   [WorkflowUpdate("RunAgent")]                               │
│                                                              │
│   6. requestEntry =                                          │
│        AgentSessionRequest.FromRunRequest(request, ...)      │
│   7. await base.RunTurnAsync(requestEntry, chatOptions: null)│
│        Inside the inherited base helper:                     │
│          await WaitConditionAsync(() => !_isProcessing)      │  ← Serialize
│          _isProcessing = true                                │
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
│      (List<ChatMessage> seeded from _history)                │
│                                                              │
│   9. Drive the durable loop:                                 │
│      for (iteration = 0; iteration < MaxToolCallsPerTurn; ++):│
│        stepInput = new AgentStepInput {                      │
│          AgentName, Request = runRequest,                    │
│          AccumulatedMessages = accumulated,                  │
│          SerializedStateBag = _currentStateBag }             │
│                                                              │
│        stepResult = await Workflow.ExecuteActivityAsync(     │
│          (AgentActivities a) =>                              │
│             a.RunDurableAgentStepAsync(stepInput))           │
│                                                              │
│        _currentStateBag = stepResult.UpdatedStateBag         │
│        accumulated.Add(stepResult.AssistantMessage)          │
│                                                              │
│        if (stepResult.IsFinal) → return AgentResponse        │
│                                                              │
│        // Fan out tool calls — see Durable Workflow Loop §   │
│        toolResults = await Workflow.WhenAllAsync(            │
│          stepResult.ToolCalls.Select(tc =>                   │
│            Workflow.ExecuteActivityAsync(                    │
│              (AgentActivities a) =>                          │
│                a.InvokeAgentToolAsync(toolInput))))          │
│                                                              │
│        accumulated.Add(new ChatMessage(Tool,                 │
│          functionResultContents))                            │
│      // loop back                                            │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentActivities.RunDurableAgentStepAsync  [Activity]       │
│                                                              │
│   10. ResolveDurableAgent(input.AgentName)                   │
│       → CachedDurableAgent (lazy via ComposeDurableAgent)    │
│         ├─ user-supplied IChatClient (from agent.ChatClient) │
│         ├─ AIContextProviders (from agent.AddContextProvider)│
│         └─ ChatClientAgent { UseProvidedChatClientAsIs=true }│
│   11. Parse sessionId from input.SessionId or WorkflowId     │
│   12. session = TemporalAgentSession.FromStateBag(           │
│         sessionId, input.SerializedStateBag)                 │
│   13. messages = input.AccumulatedMessages                   │
│       (+ AIContextProvider.InvokingAsync messages)           │
│   14. Set TemporalAgentContext.Current (for tools)           │
│                                                              │
│   15. chatClient.GetStreamingResponseAsync(messages,         │
│         per-step ChatOptions stamped with Tools/Instructions)│
│       (Workflow owns the tool loop, so the model returns     │
│        FunctionCallContent rather than auto-invoking tools.) │
│                                                              │
│   16. Stream chunks, ctx.Heartbeat(update.Text) on each      │  ← Heartbeat (always)
│       ├─ Without handler: collect into AgentStepResult       │
│       └─ With handler: also call OnStreamingResponseUpdateAsync│
│                                                              │
│   17. Return AgentStepResult {                               │
│         IsFinal, AssistantMessage, ToolCalls,                │
│         UpdatedStateBag, Usage }                             │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow.ExecuteTurnAsync (after loop completes)      │
│                                                              │
│   18. return AgentResponse {                                 │
│         Messages = allTurnMessages,                          │
│         Usage    = totalUsage,                               │
│         CreatedAt = Workflow.UtcNow }                        │
│                                                              │
│   Back in the base's RunTurnAsync:                           │
│   19. responseEntry =                                        │
│        BuildResponseEntry(corrId, output, Workflow.UtcNow)   │
│        ├─ Subclass override returns                          │
│        │   AgentSessionResponse.FromAgentResponse(...)       │
│   20. _history.Add(responseEntry)                            │  ← Record response
│   21. _isProcessing = false                                  │  ← Release gate
│   22. return response                                        │  ← Update returns
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

This uses a `WorkflowSignal` instead of a `WorkflowUpdate`:

```
Client → SignalAsync(RunFireAndForget) → Workflow receives signal
                                        → Kicks off ProcessFireAndForgetAsync as detached task
                                        → Returns immediately (no response to caller)
```

The signal handler starts a background task inside the workflow that follows the same pattern (serialize via `_isProcessing`, execute activity, record history) but with no return value.

---

## Durable Agent Composition

### Blueprint construction and per-step composition

In v0.3, the durable-agent dispatch path does not accept or cache a caller-built `AIAgent`.
`AgentActivities` first builds an immutable blueprint that caches only registration and durable-tool
shape; it does not resolve a chat client or construct middleware. For every LLM-step activity
attempt it then:

1. Creates an activity DI scope and resolves `IChatClient` through `registration.ChatClient`.
2. Resolves each `AIContextProvider` through its factory from that scope, restores the `TemporalAgentSession` StateBag, and invokes the providers explicitly before and after the model call.
3. Clones `registration.ChatOptions`, stamps `Instructions` and the selected durable tools, and passes that complete effective value through `ChatClientAgentRunOptions`. The fresh `ChatClientAgent` is built with `ChatOptions = null`, `AIContextProviders = null`, and `UseProvidedChatClientAsIs = true`, so MAF cannot merge a second default tool/options set into the request.
4. Builds and validates one configured `AIAgent` decorator chain from the same activity scope,
   then disposes its supported owned wrapper (`OpenTelemetryAgent`) before disposing the scope.

Worker startup performs one independent dry-run build per registered pipeline using a validation
scope. There is no additional first-dispatch blueprint build. Consequently middleware is
constructed once at startup and once per activity attempt; retries construct a new chain.

The explicit provider loop is intentional: it makes StateBag serialization and the durable-tool boundary visible to `AgentActivities`, rather than allowing MAF's internal agent loop to own them.

`UseProvidedChatClientAsIs = true` is load-bearing. Without it, MAF would auto-wrap the chat client in `FunctionInvokingChatClient`, which would execute tools inside the `IChatClient` pipeline — defeating the whole point of the v0.3 design where the **workflow** owns the tool-dispatch loop and each tool call becomes its own `InvokeAgentTool` activity.

### Per-step `ChatOptions` shaping

Per-turn tool filtering and response format are applied inside `RunDurableAgentStepAsync` itself, **not** through a `DelegatingAIAgent` wrapper. The activity clones `registration.ChatOptions` per step and rewrites three fields based on the originating `RunRequest`:

```csharp
// Inside RunDurableAgentStepAsync:
var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
chatOptions.Instructions = registration.Instructions;
chatOptions.Tools = AgentRunToolSelectionPolicy.FilterProviderTools(
    cached.Tools.Values.Cast<AITool>().ToList(),
    input.Request.EnableToolCalls,
    input.Request.EnableToolNames);
chatOptions.ResponseFormat = input.Request.ResponseFormat;
```

`TemporalAgentRunOptions.EnableToolCalls` and `EnableToolNames` are copied onto `RunRequest` by `TemporalAIAgentProxy` / `TemporalAIAgent` before dispatch. Session workflows freeze them on `AgentSessionRequest`, so they survive Temporal serialization and reconstruction; job and containing-workflow paths carry the same `RunRequest` directly. `EnableToolCalls = false` exposes no tools. `EnableToolNames = null` exposes all registered tools, an empty list exposes none, and a non-empty list exposes only case-insensitive registered matches.

MAF durable tool names remain case-insensitive throughout selection and policy application.
`RequireApproval`, `SkipInterceptor`, interceptor timeout, and per-tool activity retry/timeout
lookups use ordinal case-insensitive matching explicitly after Temporal deserialization; they do
not rely on a dictionary comparer surviving the JSON boundary.

Provider filtering is not the security boundary. A model can still return a malformed, unknown, or previously visible function name. Immediately before interceptor fan-out, each of the three workflow loops (`AgentWorkflow`, `AgentJobWorkflow`, and `TemporalAIAgent`) applies the same deterministic policy against the frozen registered names. A blocked call schedules no interceptor, approval, or tool activity and receives the same tenant-visible synthetic result whether its name is unknown or merely excluded.

### Discovering session context from inside a tool

Tools dispatched in `InvokeAgentToolAsync` need to discover their workflow context — to call `TemporalAgentContext.Current.RequestApprovalAsync(...)`, to read the session ID, etc. Two mechanisms cover this:

- `TemporalAgentContext.Current` (an `AsyncLocal`) is set by `RunDurableAgentStepAsync` before the LLM call and by `InvokeAgentToolAsync` before each tool call. It carries `IServiceProvider`, the agent name, the session ID, and the activity execution context.
- `TemporalAgentSession.GetService(typeof(TemporalAgentSessionId))` returns the session ID directly. The session is the `AgentSession` instance that the activity restores from `AgentStepInput.SerializedStateBag` at the start of each step.

There is no `AgentWorkflowWrapper` interposed between the `ChatClientAgent` and the user's `IChatClient` in v0.3. Application code that needs to decorate the `IChatClient` should do so by returning a decorated client from `agent.ChatClient` — see [`docs/how-to/MAF/llm-call-interception.md`](../../how-to/MAF/llm-call-interception.md).

---

## Durable Agent Workflow Loop

In v0.3 every agent registered via `TemporalAgentsOptions.AddDurableAgent(...)` runs in **durable mode**: the agentic loop lives inside `[Workflow]` code, each LLM call is its own `RunDurableAgentStep` activity, and each tool call is its own `InvokeAgentTool` activity dispatched in parallel via `Workflow.WhenAllAsync`. There is no opt-in flag — this is the only worker-hosted agent-definition path; client-only processes declare proxies separately.

### Why the loop must live in the workflow

Temporal has a hard constraint: **activities cannot schedule child activities**. Only a workflow can call `Workflow.ExecuteActivityAsync`. There is no in-activity API for "run this thing as another activity and wait." The `TemporalCommunity.Extensions.AI` durable-tools pattern works exactly because the workflow (`DurableChatWorkflow`) owns the dispatch — the activity that drives the LLM call cannot fan out to per-tool activities of its own.

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
│   for (iteration = 0; iteration < MaxToolCallsPerTurn; ++iteration):       │
│                                                                            │
│     ① Workflow.ExecuteActivityAsync(                                       │
│          (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput))     │
│                                                                            │
│           Inside the activity:                                             │
│           ├─ ResolveDurableAgent(name) → CachedDurableAgent (lazy compose) │
│           │   ├─ user-supplied IChatClient                                 │
│           │   ├─ UseAIContextProvider(providers)   ← per-step lifecycle    │
│           │   └─ ChatClientAgent { UseProvidedChatClientAsIs = true }      │
│           ├─ Clone agent.ChatOptions; stamp Instructions / Tools / format  │
│           ├─ Call agent.ChatClient.GetStreamingResponseAsync(messages)     │
│           ├─ Heartbeat per chunk                                           │
│           └─ Return AssistantMessage + FunctionCallContent[]               │
│                                                                            │
│     if (stepResult.IsFinal) → return AgentResponse                         │
│                                                                            │
│     ② Workflow.WhenAllAsync(                                               │
│          stepResult.ToolCalls.Select(tc =>                                 │
│            Workflow.ExecuteActivityAsync(                                  │
│              (AgentActivities a) =>                                        │
│                a.InvokeAgentToolAsync({ AgentName, ToolName = tc.Name,     │
│                                        Arguments = tc.Arguments,           │
│                                        CallId    = tc.CallId }),           │
│              ResolveDurableToolActivityOptions(tc.Name))))                 │
│                                                                            │
│     ③ accumulated.Add(assistantMessage);                                   │
│        accumulated.Add(toolResultMessage);  // FunctionResultContent items │
│        // loop back to ①                                                   │
│                                                                            │
│   ━━ 2N+1 activities for N tool rounds — each retryable separately ━━     │
│                                                                            │
└────────────────────────────────────────────────────────────────────────────┘
```

The mental model: **the workflow owns the tool-dispatch loop** so tool calls return raw to the workflow rather than being auto-dispatched inside the LLM call. The activity calls `IChatClient.GetStreamingResponseAsync` directly with `ChatOptions.Tools` populated from the agent's per-agent tool registry. The model returns `FunctionCallContent` items; nothing executes them inside the activity. The workflow reads those items back, fans out one `InvokeAgentTool` activity per tool call, awaits them via `Workflow.WhenAllAsync`, builds `FunctionResultContent` messages from the results, and loops.

### Two activity types

The durable path dispatches two activity types per turn:

| Activity name | Role |
|---|---|
| `TemporalCommunity.Extensions.Agents.RunDurableAgentStep` | One LLM call. Returns either a final assistant message or `FunctionCallContent[]`. Pipeline includes `UseAIContextProvider`. |
| `TemporalCommunity.Extensions.Agents.InvokeAgentTool` | One tool dispatch. Resolves the tool from the agent's local registry (per-agent — names do not collide across agents). |

The split is intentional: `InvokeAgentTool` carries the `AgentName` so the cached agent state can resolve the tool against its local registry. Two agents on the same worker can register tools with the same `AIFunction.Name` without collision, and the Temporal Web UI shows `RunDurableAgentStep` and `InvokeAgentTool` as distinct rows so operators can read the dispatch shape at a glance.

### Why `Workflow.WhenAllAsync` and not `Task.WhenAll`

The fan-out step uses `Workflow.WhenAllAsync` — the Temporal SDK's workflow-safe combinator — not `Task.WhenAll`:

```csharp
var toolOutputs = await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);
```

Inside a Temporal `[Workflow]`, `await`s must run on the workflow scheduler so that task continuations are deterministic on replay. `Task.WhenAll` is technically safe in many cases (when all the awaited tasks come from `Workflow.ExecuteActivityAsync`, which schedules continuations on `TaskScheduler.Current`), but `Workflow.WhenAllAsync` is the project convention and is documented as "the workflow-safe equivalent of `Task.WhenAll`" by the SDK itself. `WorkflowAgents.ExecuteAgentsInParallelAsync` (`src/TemporalCommunity.Extensions.Agents/WorkflowAgents.cs:112`) already uses it for the parallel-agent pattern; the durable loop follows the same convention.

`Workflow.WhenAllAsync` preserves input order. The result list is index-aligned with the input task list, so the workflow can pair `toolOutputs[i]` with `toolCalls[i]` to build `FunctionResultContent(callId: toolCalls[i].CallId, result: toolOutputs[i].Result)` without needing a correlation lookup.

### The iteration cap as a workflow-history bound

Temporal's per-workflow event-history limit (~50K events) is a hard constraint on any in-workflow loop. Each iteration in the durable loop contributes:

- One `ActivityScheduled` + one `ActivityCompleted` (or `ActivityFailed` + retries) for the `RunDurableAgentStep` call
- One pair of events per tool call in the fan-out batch

A two-tool turn that converges in one round costs ~6 events. A model that loops indefinitely on tool calls would consume the history budget and force a continue-as-new mid-turn, which is harder to reason about than a clean structured failure.

`MaxToolCallsPerTurn` (default `20`, configured per-agent on `DurableAgentBuilder`) bounds the loop counter. When the cap is exceeded, the workflow does not throw; it appends an `assistant` message of the form

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

The durable loop in `AgentWorkflow.cs` follows all of these rules: every `await` either omits `ConfigureAwait` or uses `ConfigureAwait(true)`; the loop counter is a local `int`; iteration timestamps for the final `AgentResponse.CreatedAt` come from `Workflow.UtcNow`; and there is no in-workflow OTel span (the `agent.turn` span fires inside `RunDurableAgentStepAsync`, which runs in activity context).

### Continue-as-new across durable mode

`AgentWorkflowInput.DurableAgentToolActivityOptions` is built once by the client at workflow start (from the agent's per-tool `DurableToolOptions`) and carried through `CreateContinueAsNewException` so the per-tool retry constraints survive CAN transitions. A write tool registered with `opts.NoRetry()` keeps `MaximumAttempts = 1` across every continue-as-new boundary — the options dictionary is never re-read from the registration, so registration-time changes do not bleed into running workflows. Continue-as-new is settings-frozen.

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

**What it controls**: Maximum wall-clock time for a single `RunDurableAgentStepAsync` (or `InvokeAgentToolAsync`) activity execution, measured from when the worker starts executing the activity to when it must return a result.

**What happens on timeout**: Temporal marks the activity as failed. The workflow's `ExecuteActivityAsync` call throws a `TimeoutException`. Since there is no retry policy configured by default, the activity is **not** automatically retried — the workflow itself fails.

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
List<AgentResponseUpdate> collectedUpdates = [];
await foreach (var update in responseStream.WithCancellation(ct))
{
    collectedUpdates.Add(update);
    ctx.Heartbeat(update.Text);    // ← Heartbeat fired on every chunk
}
response = collectedUpdates.ToAgentResponse();
```

**What happens on heartbeat timeout**: Temporal cancels the activity's `CancellationToken` and marks it as timed out. This is the primary mechanism for detecting a dead worker during long LLM calls. Caller-visible `RunStreamingAsync` is intentionally unsupported.

**Key insight**: `HeartbeatTimeout` is active while the model-step activity consumes provider updates. It is not a caller-visible streaming transport.

#### 3. Workflow `TimeToLive` (default: 14 days)

```csharp
TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
    timeout: ttl);
```

**What it controls**: How long the workflow stays alive waiting for new messages. This is not a Temporal-enforced timeout — it is the `timeout` parameter to `WaitConditionAsync`.

**What happens when TTL expires**: The wait returns `false`, the workflow logs "TTL expired", and completes normally. The session is done. Any subsequent message to this session ID will start a **new** workflow (because `IdReusePolicy = AllowDuplicate`).

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
4. Temporal marks the activity task as failed
5. The workflow is now blocked on `ExecuteActivityAsync`, waiting for a result
6. A new worker picks up the workflow task from the task queue
7. The new worker **replays** the workflow from the beginning:
   - All prior completed activities return cached results from history
   - The failed activity is **rescheduled** (new execution attempt)
8. The activity runs again on the new worker (fresh LLM call)
9. If it succeeds, the result is recorded and the workflow continues

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
Client waiting on ExecuteUpdateAsync → AgentWorkflow.RunAgentAsync running → [WORKER DIES]
```

**Timeline:**

1. Client is blocking on `handle.ExecuteUpdateAsync(wf => wf.RunAgentAsync(request))`
2. Worker crashes mid-update
3. New worker picks up the workflow, replays, and the update handler re-executes
4. Once the update completes on the new worker, the response is delivered to the waiting client

**Client experience**: The `ExecuteUpdateAsync` call blocks until the update completes (even across worker failures). The client does not need retry logic — Temporal handles the handoff transparently.

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
| `StartToCloseTimeout` | 5 min | Single activity | Stuck/slow LLM call | `TemporalAgentsOptions.DefaultActivityTimeout` |
| `TimeToLive` | 14 days | Entire workflow | Session inactivity | `TemporalAgentsOptions.DefaultTimeToLive` or per-agent |

| Crash Scenario | Data Loss | Recovery | Automatic? |
|---------------|-----------|----------|------------|
| Worker dies during activity | None | Activity retried on new worker | Yes |
| Worker dies between activities | None | Workflow replayed, cached results returned | Yes |
| Worker dies during update | None | Update re-executes on new worker, client blocks until done | Yes |
| Temporal server restarts | None | Workers reconnect, workflows resume | Yes |
| Client disconnects during update | Possible duplicate request | Client re-sends update; serialized via `_isProcessing` | Manual |

---

## Related Documentation

- [durability-and-determinism.md](./durability-and-determinism.md) — Step-by-step walkthrough of deterministic replay with agent calls
- [CLAUDE.md](../../../CLAUDE.md) — Project architecture overview and quick reference

---

_Last updated: 2026-05-10_
