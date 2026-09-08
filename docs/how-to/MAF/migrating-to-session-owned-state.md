# Migrating to session-owned state (v0.3 → v0.4)

In v0.3, `TemporalAIAgent` held the conversation history and the StateBag on the **agent
instance**. In v0.4 both belong to the **session**.

This page covers what changes for you, what breaks, and how to roll it out safely.

---

## Why it changed

`WorkflowAgents.GetTemporalAgent("Name")` returns one agent instance per workflow. Calling
`CreateSessionAsync()` twice on it gave you two session objects with two distinct session IDs — but
both conversations wrote into the same `_history` list and the same `_currentStateBag` field on the
agent. Three consequences:

- **Cross-session contamination.** The second conversation's prompt replayed the first one's turns,
  and its context-provider StateBag writes overwrote the first one's.
- **State loss at continue-as-new.** Neither field was part of the serialized session, so carrying a
  session across a continue-as-new boundary silently dropped everything.
- **Distinct session IDs implied an isolation the implementation never provided.**

The session is the natural owner: it is the unit MAF already serializes, replays, and hands back.

---

## What you have to change

### 1. Pass a `TemporalAgentSession`

`TemporalAIAgent.RunAsync` now requires the session to be a `TemporalAgentSession`. A foreign
`AgentSession` has nowhere to hold the conversation's state, so it is rejected instead of silently
dropping every mutation.

```csharp
var agent = WorkflowAgents.GetTemporalAgent("SubAgent");
var session = await agent.CreateSessionAsync();          // returns a TemporalAgentSession
await agent.RunAsync([new ChatMessage(ChatRole.User, "hi")], session);
```

If you omit the session entirely, the agent still creates one for you — but it is then a *new*
session per call, so nothing accumulates. Create the session once and reuse it for the conversation.

`SerializeSessionCoreAsync` already rejected non-`TemporalAgentSession` values in v0.3, so code that
round-tripped its sessions was already conforming.

### 2. One run at a time per session

Two overlapping `RunAsync()` calls on the **same** session now throw `InvalidOperationException`:

```
Overlapping RunAsync() calls on the same session are not allowed.
Use distinct session objects for parallel conversations.
```

They would otherwise append to one history and merge into one StateBag with no defined ordering.
Parallel conversations on the *same agent* are fully supported — give each one its own session:

```csharp
var a = await agent.CreateSessionAsync();
var b = await agent.CreateSessionAsync();
await Task.WhenAll(
    agent.RunAsync(messagesA, a),
    agent.RunAsync(messagesB, b));   // fine: distinct sessions
```

### 3. Carry the session across continue-as-new

State survives a continue-as-new only if you carry it. Serialize the session into your workflow's
continue-as-new input and restore it on the far side:

```csharp
var serialized = await agent.SerializeSessionAsync(session);
throw Workflow.CreateContinueAsNewException((MyWorkflow wf) => wf.RunAsync(
    new MyInput { CarriedSession = serialized }));

// ...on the new run:
var session = input.CarriedSession is { } carried
    ? await agent.DeserializeSessionAsync(carried)
    : await agent.CreateSessionAsync();
```

This is explicit on purpose — the orchestrating workflow owns its own continue-as-new policy, and
the library does not guess when to carry a sub-agent conversation forward.

---

## Wire compatibility

The persisted session format keeps its v0.3 property names and value shapes:

```jsonc
// v0.3
{"sessionId":"ta-assistant-abc123","stateBag":{"k":"v"}}

// v0.4 — same two members, plus history when the session has any
{"sessionId":"ta-assistant-abc123","stateBag":{"k":"v"},"history":[ /* entries */ ]}
```

- **v0.4 reads v0.3.** A snapshot with no `history` member restores with an empty history. A v0.3
  snapshot that wrote `"stateBag":{}` restores as an empty bag. Both are covered by tests.
- **`stateBag` is now omitted when empty** rather than written as `{}`. Both forms read correctly.

### Rolling deployments: read this before you deploy

A **v0.3 worker cannot see v0.4 history.** The v0.3 reader pulls `sessionId` and `stateBag` by name
and ignores members it does not recognise, so it accepts a v0.4 payload and **silently drops the
`history` member**. There is no version marker in the format to make this loud.

In practice that means: if a v0.4 worker persists a session across a continue-as-new boundary and a
v0.3 worker picks up the next run, that conversation is truncated to nothing, with no error.

**Deploy all workers on a task queue to v0.4 before any of them starts persisting session history.**
The usual safe pattern is a task-queue cutover — stand up v0.4 workers on a new queue and drain the
old one — rather than a mixed-version rolling replace.

---

## A serialized session now contains the conversation

This is the change most likely to matter outside the code.

In v0.3, `SerializeSessionAsync` produced an identifier and a StateBag. In v0.4 it produces those
plus **the full conversation transcript** — user messages, model replies, tool arguments, and tool
results.

If you persist sessions anywhere — Redis, Postgres, blob storage, a cache, a log — that store now
holds conversation content, which for most applications means user data and quite possibly PII.
Re-check:

- **Encryption at rest** on the session store.
- **Retention and deletion**, including whatever right-to-erasure obligations apply.
- **Access controls** on anything that can read a serialized session.
- **Log hygiene** — a serialized session is no longer safe to dump into a debug log.

There is no redaction hook. If you need one, serialize the StateBag alone
(`session.StateBag.Serialize()`) and accept that history will not survive.

Payload growth is linear at roughly 750 bytes per turn for short messages. A carried session
reaches Temporal's default 256 KB payload *warning* threshold near 350 turns and the 2 MB *error*
threshold near 2,800 turns. History is uncompacted in v0.4, so long-running conversations that
continue-as-new should bound turns per generation. This is separate from the existing 64 KB
`CarriedStateBag` size guard, which is unchanged and does **not** cover session history.

---

## Sessions are bound to their agent

`TemporalAIAgent.RunAsync` now rejects a session created by a differently-named agent:

```
The provided session belongs to agent 'Researcher', not agent 'Summarizer'.
Create the session from the same agent that will run it.
```

`TemporalAIAgentProxy` has always enforced this. `TemporalAIAgent` did not, which was tolerable when
a mismatched session carried only an ID — and is not, now that it carries a transcript that would
otherwise be replayed into the wrong agent's prompt and event history.

Matching is by agent **name**, case-insensitively, so two agent instances resolved from the same
registration (`GetTemporalAgent("X")` called twice) still share sessions freely.

---

## Serialization options

`SerializeSessionAsync` / `DeserializeSessionAsync` accept a `JsonSerializerOptions`. The
`agent_request` / `agent_response` discriminators for history entries are registered at runtime by
`TemporalAgentJsonUtilities`, not by attributes, so options that lack that registration cannot
round-trip history — serializing would throw `NotSupportedException`.

The session uses your options as given only when they satisfy **both** halves of the snapshot
contract:

1. `TemporalAgentSessionSnapshot` resolves from the generated `AgentSessionJsonContext` — not from
   the reflection resolver. Accepting reflection-backed options here would serialize the snapshot
   root through reflection and reintroduce the AOT and trimming exposure that registering the DTO
   exists to remove.
2. `DurableSessionEntry` carries the `agent_request` / `agent_response` derived types, so history
   entries round-trip.

Neither implies the other — options can declare the discriminators and still resolve the snapshot
root reflectively. When either check fails the session falls back to
`TemporalAgentJsonUtilities.DefaultOptions`. Options derived from
`TemporalAgentJsonUtilities.DefaultOptions` copy the whole resolver chain and satisfy both, so
derive from it if you pass custom options.

When the fallback does engage, your `Encoder` and `MaxDepth` are carried across onto the derived
options. The library default uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, which leaves `<`,
`>`, and `&` unescaped; silently substituting it would strip a stricter encoder you had chosen —
and the payload now carries model- and tool-authored text. Settings that would change the wire
shape itself (naming policy, reference handling, the resolver chain) are not carried across: the
snapshot format is the library's contract.

---

## StateBag semantics

Two things to know if you write context providers or tools that touch the StateBag.

**Context-provider output is overlaid, not substituted.** After each LLM step the activity's
StateBag is merged onto the session's bag key by key. Keys the step did not mention are preserved.
Previously `TemporalAIAgent` replaced the bag outright, so a hash-gated step returning a partial bag
would wipe state written between activities. A consequence worth knowing: a provider can no longer
delete a key by omitting it.

This also removes an inconsistency rather than introducing one. The long-lived `AgentWorkflow` path
has always overlaid (`AgentWorkflow.cs`, LLM-step handling); `TemporalAIAgent` was the outlier. The
two agent paths now behave identically.

**Tool write-backs merge in tool-call index order, and later index wins.** Tool activities fan out
concurrently, so completion order differs between the original run and replay and must never drive
the merge. Reserved approval-scope keys are still dropped from tool and interceptor write-backs.

---

## Unchanged

- History is still uncompacted and unbounded per session. The 64 KB StateBag size guard is
  unchanged. Compaction remains future work.
- `TemporalAIAgentProxy` behaviour is unchanged; it shares the same session wire shape.
- Worker-level configuration (`MaxToolCallsPerTurn`, per-tool activity options, interceptor
  configuration) stays agent-scoped, because it describes the agent rather than the conversation.
- **Tools invoked by a `TemporalAIAgent` sub-agent still cannot read or write the session StateBag.**
  `InvokeAgentToolInput` carries no session ID, so the tool activity derives its session from the
  activity's workflow ID — which for a sub-agent is the *orchestrating* workflow, not a
  `ta-{agent}-{key}` session. No `TemporalAgentContext` is established and the tool's write-back
  comes back empty. This predates v0.4 and is unchanged by it; giving the session a StateBag did not
  make it reachable from tools on this path. Context providers are unaffected — the LLM-step
  activity does receive an explicit session ID, which is why provider state threads correctly.
  Tools running under the long-lived `AgentWorkflow` path are also unaffected.
