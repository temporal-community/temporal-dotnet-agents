# TemporalAgentSession, AgentSessionStateBag, and AIContextProvider

This document explains how these three collaborating concepts fit together, why the design works the way it does, and how data flows and is restored across conversation turns.

---

## The Three Collaborators

| Class | Layer | Responsibility |
|---|---|---|
| `TemporalAgentSession` | TemporalAgents | Temporal-specific `AgentSession`; carries `SessionId` and `StateBag`; created fresh per activity |
| `AgentSessionStateBag` | Microsoft Agent Framework | Thread-safe key-value store; holds per-session state across activity attempts and workers |
| `AIContextProvider` | Microsoft Agent Framework | Prepares context once per logical turn and observes terminal turn completion |

They are not a chain — they are **three roles** inside a single execution pipeline. The `TemporalAgentSession` *contains* the `StateBag`; the `AIContextProvider` *reads from and writes to* that `StateBag`.

---

## Why the StateBag Exists

Provider identity depends on registration: an instance overload can reuse an instance, while a
factory resolves from fresh preparation and terminal-notification activity scopes. Neither is a
session-lifetime guarantee: activities can retry, move workers, or overlap sessions. Do not retain
per-session data in provider fields.

The `AgentSessionStateBag` is the solution: each `AgentSession` owns its own bag, and each provider uses a unique string key to store its slice of data there.

```
activity process
├── Mem0Provider (possibly shared; never session-owned)
├── AgentSession for user-A
│     └── StateBag { "Mem0Provider": { UserId:"A", AgentId:"bot" } }
└── AgentSession for user-B
      └── StateBag { "Mem0Provider": { UserId:"B", AgentId:"bot" } }
```

The provider reads the right state by keying into the session's bag — it never holds session-specific data itself.

---

## The AIContextProvider Two-Phase Lifecycle

The managed workflow owns the two hooks; it does not let `ChatClientAgent` run them around every
model step. Hook invocations can repeat on an activity retry, but completed activity results replay
from history.

### Turn preparation (`InvokingAsync`)

`PrepareAgentTurn` restores the session StateBag and seeds registered tools, messages, and
instructions. Providers enrich or filter that context. The activity records the final selection
and key-level StateBag delta; every model step in the turn reuses that prepared context.

### Terminal turn notification (`InvokedAsync`)

`CompleteAgentTurn` notifies providers after success, failure, or cancellation, with the terminal
messages and exception as applicable. Factories may return a different provider instance than
preparation did. Session state must travel through the StateBag, not instance fields. Generic
provider-owned external history/writes are unsupported without a dedicated durable adapter.

```
AgentWorkflow logical turn
  PrepareAgentTurn: InvokingAsync -> recorded context and StateBag delta
  RunDurableAgentStep -> serial tool activities -> next model step
  CompleteAgentTurn: InvokedAsync -> terminal StateBag delta
  Commit success, or restore pre-turn StateBag on failure/cancellation
```

### How `AIContext` output is applied (Wave C behaviour)

The three fields on the `AIContext` returned by `InvokingAsync` are handled differently by the durable-agent loop in `AgentActivities`:

> **`AIContext.Instructions`** — applied. The final aggregated `Instructions` (after the full provider chain completes) is assigned to `ChatOptions.Instructions` before the LLM call, replacing the agent's base-registered instructions for that step. Because each provider receives the previous provider's `AIContext` as its `InvokingContext`, providers naturally compose — provider N+1 sees provider N's `Instructions` and can extend or replace it.
>
> **`AIContext.Messages`** — applied. The final aggregated `Messages` list becomes the conversation context passed to the LLM. Same chaining semantics as `Instructions`.
>
> **`AIContext.Tools`** — selects registered durable tools for the turn. New untraceable
> functions fail before model dispatch. To supply provider-owned durable tools, implement `IDurableToolSource` on
> the provider or pass `DurableToolRegistrationSpec` values to
> `AddContextProvider(provider, durableTools)`. The builder registers those functions through
> the same per-tool activity path as `agent.AddTool()`.

---

## What Gets Stored Where

This is the most important distinction in the whole system:

| Data | Where it lives | Why |
|---|---|---|
| Provider scoping params (e.g. `UserId`, `AgentId`) | `StateBag` | Lightweight; needed to address the external service; must survive across turns |
| Actual memories / extracted facts | External Mem0 service | Heavy; semantically indexed; cross-session and cross-agent |
| Conversation turn history | `AgentWorkflow._history` → Temporal event history | Temporal-native durability; rebuilds context window each turn |
| Raw LLM prompt + response | `DurableSessionEntry.Messages` (`IReadOnlyList<ChatMessage>`) in event history | Stored as MEAI `ChatMessage` directly on `AgentSessionRequest` / `AgentSessionResponse` instances; flattened into `allMessages` at activity start |

The `StateBag` is **not a memory store** — it is a persistent address book that tells providers where to find data in their respective external services.

---

## External-memory providers need a dedicated durable adapter

`Mem0Provider` and similar providers perform external reads and writes from their lifecycle hooks.
Those hooks run in retryable preparation/terminal activities, and this library does not provide an atomic
idempotent provider-history contract. They are therefore **not supported as direct durable
registrations**. The stock MAF `ChatHistoryMemoryProvider` is supported only through the dedicated
opt-in adapter documented in [Agent memory](../../how-to/MAF/memory.md); generic
`AddContextProvider` registration remains rejected.

The historical `Mem0Provider` material below explains StateBag mechanics only. It is not a durable
recipe. No direct `AddContextProvider` registration for external-memory providers is supported.

### Historical Mem0Provider StateBag Walkthrough

`Mem0Provider` is a `MessageAIContextProvider` (which extends `AIContextProvider`) that backs long-term memory with the Mem0 API.

### What it stores in the StateBag

```csharp
// This is the ONLY thing in the StateBag for Mem0Provider
public sealed class Mem0Provider.State
{
    public Mem0ProviderScope StorageScope { get; }  // where to write memories
    public Mem0ProviderScope SearchScope  { get; }  // where to search memories
}

// A scope is just scoping identifiers
public class Mem0ProviderScope
{
    public string? UserId        { get; set; }
    public string? AgentId       { get; set; }
    public string? ThreadId      { get; set; }
    public string? ApplicationId { get; set; }
}
```

No messages. No embeddings. Just the parameters that tell the Mem0 API which user's memories to look up.

### InvokingAsync — fetch memories

```csharp
protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
    InvokingContext context, CancellationToken ct)
{
    // Read (or initialize) the State from the session's StateBag
    var state = this._sessionState.GetOrInitializeState(context.Session);

    // Use scoping params to search the external Mem0 service
    var memories = await this._client.SearchAsync(
        state.SearchScope.ApplicationId,
        state.SearchScope.AgentId,
        state.SearchScope.ThreadId,
        state.SearchScope.UserId,
        queryText, ct);

    // Return them as injected ChatMessages (merged into the prompt)
    return [new ChatMessage(ChatRole.User,
        $"## Memories\n{string.Join('\n', memories)}")];
}
```

### InvokedAsync — store new messages

```csharp
protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct)
{
    var state = this._sessionState.GetOrInitializeState(context.Session);

    // Write request + response messages to the external Mem0 service
    // Mem0 extracts and stores meaningful facts automatically
    await this.PersistMessagesAsync(state.StorageScope,
        context.RequestMessages.Concat(context.ResponseMessages ?? []), ct);
}
```

### Turn-by-turn: what actually changes in the StateBag

On **Turn 1**, the `stateInitializer` delegate runs — the `State` object is created and written into the bag for the first time. On **Turn 2+**, `GetOrInitializeState` finds it already in the bag and returns it without calling the initializer again.

So the `StateBag` entry for `Mem0Provider` is set once and then **read-only for the rest of the session**. The interesting mutation is happening entirely inside the Mem0 service.

---

## How StateBag Survives Across Turns in TemporalAgents

### The Problem Without Persistence

In Temporal, each activity execution is a separate method call. The `TemporalAgentSession` object is constructed fresh at the start of each activity. Without explicit persistence, the `StateBag` starts empty every time — `GetOrInitializeState` would call the initializer on every single turn, as if the session had just started.

### The Solution: Serialize → Store in Workflow → Restore

The `StateBag` is passed explicitly through three layers as a `JsonElement?`:

```
AgentActivities.RunDurableAgentStepAsync
  │
  ├── START: TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag)
  │          └── if SerializedStateBag is non-null: AgentSessionStateBag.Deserialize(bagEl)
  │          └── if null (first step):              new AgentSessionStateBag()
  │
  ├── ... AIContextProvider runs, reads/writes the restored StateBag ...
  │
  └── END: session.SerializeStateBag()
           └── if StateBag.Count == 0: returns null  (no wasted bytes)
           └── if StateBag.Count > 0: returns JsonElement via StateBag.Serialize()
```

The serialized `JsonElement?` is returned on `AgentStepResult.UpdatedStateBag` and threaded back into the next iteration by `AgentWorkflow.ExecuteDurableAgentTurnAsync`:

```csharp
// AgentWorkflow stores the serialized bag after each step
_currentStateBag = stepResult.UpdatedStateBag;

// AgentWorkflow passes it to the next step activity
var stepInput = new AgentStepInput
{
    AgentName           = _input.AgentName,
    Request             = runRequest,
    AccumulatedMessages = accumulated,
    SerializedStateBag  = _currentStateBag,   // ← restored in next activity
};
```

### Session identity through agent middleware

The restored `TemporalAgentSession` is also the exact `AgentSession` passed through outer
`ConfigureAgentPipeline` middleware. `TemporalAgentContext.Current.CurrentSession` refers to that
same object. Middleware may write retry-safe values to its `StateBag`; the activity serializes
those changes into `AgentStepResult.UpdatedStateBag`, so the next step restores them.

StateBag persistence is committed at the **turn** boundary. If a model, provider, interceptor,
tool, response projection, or workflow update fails the turn after its activity retries are
exhausted, the workflow restores application-, provider-, interceptor-, and tool-owned StateBag
entries to their pre-turn values. A later turn therefore never observes partial state from the
failed turn.
The workflow captures and restores the StateBag snapshot while holding the same serialized-turn
gate that protects conversation history. A second Update may be accepted while the first turn is
waiting on an activity, but it cannot capture its rollback snapshot until the first turn has
committed. If that queued Update fails, its rollback therefore cannot erase StateBag changes made
by the preceding successful turn.

The library inserts an innermost session boundary immediately above `ChatClientAgent`.
`ChatClientAgent` accepts only its own sealed `ChatClientAgentSession`, so the boundary forwards
`null` only at that final leaf and MAF creates a transient leaf session. Middleware outside the
boundary never receives that transient session. It must forward the exact supplied
`TemporalAgentSession`; replacing it or passing `null` is rejected because the replacement's
StateBag could not be persisted unambiguously. Session lifecycle APIs on this internal activity
pipeline are unsupported because Temporal, not the MAF leaf, owns durable session serialization.
The boundary restores the durable run context to surrounding middleware after success, provider
exceptions, and cancellation. Public durable-agent streaming is unsupported; the model-step
activity calls non-streaming `RunAsync`, so caller-abandoned enumeration is not part of this
package's session-context contract.

### Across Continue-as-New

When Temporal workflow history grows too large, `AgentWorkflow` triggers a continue-as-new. The `StateBag` survives this via `AgentWorkflowInput.CarriedStateBag`:

```csharp
// On continue-as-new
var carriedStateBag = _currentStateBag;

throw Workflow.CreateContinueAsNewException<AgentWorkflow>(
    wf => wf.RunAsync(new AgentWorkflowInput
    {
        AgentName      = _agentName,
        CarriedHistory = trimmedHistory,
        CarriedStateBag = carriedStateBag,   // ← survives workflow restart
        // ...
    }));
```

On the other side, the new workflow run restores it immediately:

```csharp
// AgentWorkflow constructor
_currentStateBag = input.CarriedStateBag;
```

### Full Persistence Diagram

```
Turn 1                   Turn 2                   Continue-as-New       Turn N
─────────────────────    ─────────────────────    ──────────────────    ─────────────────
Activity starts          Activity starts           Workflow restarts     Activity starts
  session = FromStateBag   session = FromStateBag    _currentStateBag      session = FromStateBag
  (null → empty bag)       (bag restored ✓)          → CarriedStateBag     (bag restored ✓)
                                                      → _currentStateBag
Provider runs            Provider runs
  initializer called       bag already has State    Provider runs         Provider runs
  State written to bag     no initializer call      bag still intact      no initializer call

Activity ends            Activity ends
  key delta recorded       key delta recorded
  → activity result        → activity result

Workflow stores          Workflow stores
  merges key delta         merges key delta
  into _currentStateBag    into _currentStateBag
```

---

## Durable Registration Boundary

`AIContextProvider` instances are registered on `DurableAgentBuilder` through `AddContextProvider`.
`PrepareAgentTurnAsync` and `CompleteAgentTurnAsync` own the logical-turn provider lifecycle and
return key-level StateBag deltas. Model, interceptor, and serial tool activities also write back
state. The library does not hand provider lifecycle ownership to `ChatClientAgent`.

### Approval limits

Workflow-parked approvals are owned by the registered `AgentWorkflow` and are available through a
managed session invoked with `TemporalAIAgentProxy`. Regular workflows should invoke that proxy
from an activity; there is no workflow-local agent execution path.

Direct registration of `Mem0Provider` or another provider that persists history or writes to an
external store is intentionally unsupported: calling those hooks from retryable activities can
duplicate or lose external effects. `ChatHistoryMemoryProvider` has a separate supported opt-in
adapter with explicit scope validation and retry semantics; see [Agent memory](../../how-to/MAF/memory.md).
The [ContextProviders sample](../../../samples/MAF/ContextProviders/) carries custom provider state
in `StateBag` without becoming the conversation-history owner.

---

## Calling CreateSessionAsync More Than Once

`CreateSessionAsync` is a **pure, stateless factory** — it builds a new `TemporalAgentSession` in local memory and returns it. The agent holds no reference to the sessions it has created. No Temporal API is called. No workflow is started. Calling it N times creates N independent session objects, each with a unique `WorkflowId`, and nothing else happens until `RunAsync` is called.

```csharp
// All three are just in-memory objects — no Temporal calls yet
var s1 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-a1b2c3...
var s2 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-d4e5f6...
var s3 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-7g8h9i...
```

A workflow only starts when `RunAsync` is called with a given session for the first time. The unused sessions are simply garbage collected — no cleanup needed, no orphaned workflows on the server.

### Behavior differs between the two agent types

#### `TemporalAIAgentProxy` — fully isolated per session

Each session maps to a separate `AgentWorkflow` instance via `WorkflowId`. Two sessions = two independent workflows = completely isolated conversation histories, StateBags, and TTL timers:

```csharp
var proxy = services.GetTemporalAgentProxy("WeatherAgent");
var s1 = await proxy.CreateSessionAsync();
var s2 = await proxy.CreateSessionAsync();

// s1 starts its own AgentWorkflow (ta-WeatherAgent-aaa)
await proxy.RunAsync("I'm planning a ski trip to Whistler.", s1);

// s2 starts a completely separate AgentWorkflow (ta-WeatherAgent-bbb)
await proxy.RunAsync("I need beach recommendations.", s2);

// s1's workflow only knows about ski trips
// s2's workflow only knows about beaches
await proxy.RunAsync("What gear do I need?", s1);  // "ski gear..."
await proxy.RunAsync("What gear do I need?", s2);  // "swimwear, snorkel..."
```

This is the correct way to serve multiple concurrent users from the same agent proxy instance: one session per user conversation, created independently, never shared.

#### `TemporalAIAgentProxy` + external memory: isolated conversation, shared or isolated memories

Conversation history is always isolated per session. Whether the **external memory** (e.g. Mem0) is isolated depends entirely on your `stateInitializer` scoping:

```csharp
// ⚠️ SHARED external memories — both sessions resolve to the same userId
stateInitializer: session => new Mem0Provider.State(
    new Mem0ProviderScope { UserId = "user-alice" })
//  ↑ static value — both s1 and s2 read/write the same Mem0 memories

// ✅ ISOLATED external memories — each session gets its own thread scope
stateInitializer: session => new Mem0Provider.State(
    new Mem0ProviderScope {
        UserId   = "user-alice",
        ThreadId = session?.GetService<TemporalAgentSessionId>()?.Key
        //         ↑ unique per session — different Mem0 memory thread each time
    })
```

Choosing between shared vs. isolated memory scopes is a product decision: shared memories let one session "remember" facts learned in another (good for a single user across multiple conversations), while isolated memories give each session a clean slate.

#### The registered proxy — history and StateBag are workflow-owned

The supported agent receiver is the registered `TemporalAIAgentProxy`. It targets the
`AgentWorkflow` identified by `TemporalAgentSessionId.WorkflowId`; the workflow owns both
conversation history and the session StateBag. Tool activities run with the managed session
context, so `TemporalAgentContext.Current` identifies the same session while the activity is
executing.

For regular workflow orchestration, resolve the proxy from the activity's scoped service provider.
The calling workflow passes a stable session workflow ID and request ID so activity retries target
the same logical turn. Agent history and StateBag continue across the registered workflow's own
continue-as-new transition.

#### A serialized session contains the conversation

`SerializeSessionAsync` emits the full transcript — user messages, model replies, tool arguments and
results — alongside the session ID and StateBag. Anywhere you persist a serialized session now holds
conversation content, which usually means user data. There is no redaction hook; serialize
`session.StateBag` alone if you need the state without the transcript.

Payload grows roughly linearly at ~750 bytes per turn for short messages, so a carried session
approaches Temporal's default 256 KB payload warning near 350 turns. History is uncompacted. This is
separate from the 64 KB `CarriedStateBag` size guard, which does not cover session history.

### Summary table

| Scenario | Conversation history | External memory (e.g. Mem0) |
|---|---|---|
| Registered proxy, two sessions | Fully isolated (separate workflows) | Depends on provider scope initialization |
| Registered proxy, same session reused | Shared (same workflow) | Shared |

---

## Multiple Providers and Key Collisions

Multiple `AIContextProvider` instances can be attached to the same agent. Each uses `StateKey` (defaults to the type name) to store independently:

```csharp
new ChatClientAgentOptions
{
    AIContextProviders =
    [
        new Mem0Provider(...),                      // StateKey = "Mem0Provider"
        new TextSearchProvider(...),                // StateKey = "TextSearchProvider"
        new Mem0Provider(...) { StateKey = "mem0-second" }  // custom key for a second instance
    ]
}
```

The bag for this session would contain:

```json
{
  "Mem0Provider":       { "storageScope": { "userId": "u1" }, "searchScope": { "userId": "u1" } },
  "TextSearchProvider": { ... },
  "mem0-second":        { "storageScope": { "userId": "u1", "agentId": "alt" }, ... }
}
```

Each provider reads only its own key — they are fully independent.

---

## Key Invariants to Remember

1. **The StateBag is not a memory store.** It holds compact per-session state for compatible providers. It does not make an external provider's reads, writes, or history durable.

2. **Initialization is persisted per session, but activity execution is at-least-once.** Once the
   initialized state is recorded in the workflow-owned StateBag, later turns reuse it instead of
   initializing again. An activity may nevertheless execute more than once before its result is
   recorded, so initializers must be repeatable and safe to retry; do not rely on exactly-once
   execution.

3. **An empty bag serializes to `null`.** `TemporalAgentSession.SerializeStateBag()` returns `null` when `StateBag.Count == 0`, avoiding unnecessary bytes in Temporal's event history on turns where no provider has written anything.

4. **The bag survives continue-as-new.** `AgentWorkflowInput.CarriedStateBag` carries it across workflow history resets — providers never need to reinitialize after a long-running session triggers continue-as-new.

5. **Providers run in activities, not workflow code.** Preparation runs before the model/tool loop;
   terminal notification runs afterward. Model calls execute in `RunDurableAgentStepAsync`.
   Provider-owned external history or writes require a dedicated adapter. Never call `RunAsync`
   directly inside a `[Workflow]` class.

---

## References

- `src/TemporalCommunity.Extensions.Agents/Session/TemporalAgentSession.cs` — `FromStateBag`, `SerializeStateBag`
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentActivities.cs` — activity entry point; restore/serialize lifecycle
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflow.cs` — `_currentStateBag`, continue-as-new transfer
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentStepInput.cs` — `SerializedStateBag` field
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflowInput.cs` — `CarriedStateBag` field
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStateBag.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/ProviderSessionState{TState}.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Mem0/Mem0Provider.cs`

---
