# MAF `HarnessAgent` compatibility

**Short answer: you cannot use `HarnessAgent` with this library, and you cannot do so by accident —
it fails to compile in both slots where you might try.**

`HarnessAgent` ships in **`Microsoft.Agents.AI.Harness`**, a package separate from the
`Microsoft.Agents.AI` this library depends on. Version `1.17.0` exists there, matching the MAF
version pinned in `Directory.Packages.props`. This library does not reference it.

Verified against the `Microsoft.Agents.AI.Harness` 1.17.0 and `Microsoft.Agents.AI` 1.17.0 package
documentation.

---

## What `HarnessAgent` is

A `DelegatingAIAgent` that wraps a `ChatClientAgent`, assembling an opinionated pipeline from one
caller-supplied `IChatClient`. Its single constructor is
`HarnessAgent(IChatClient, HarnessAgentOptions, ILoggerFactory, IServiceProvider)`.

Its chat-client pipeline, innermost first:

1. **`FunctionInvokingChatClient`** — automatic in-process tool invocation
2. `MessageInjectingChatClient`
3. `PerServiceCallChatHistoryPersistingChatClient`
4. `AIContextProviderChatClient` with a `CompactionProvider` (only when both
   `MaxContextWindowTokens` and `MaxOutputTokens` are set)

Plus context providers (`TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`,
`AgentSkillsProvider` on by default; `FileAccessProvider` and `BackgroundAgentsProvider` opt-in) and
agent decorators (`ToolApprovalAgent`, `OpenTelemetryAgent` on by default; `LoopAgent` when
evaluators are supplied).

---

## Why it cannot be plugged in

### The `ChatClient` slot rejects it

`DurableAgentBuilder.ChatClient` is a `Func<IServiceProvider, IChatClient>`. `HarnessAgent` is an
`AIAgent`, not an `IChatClient`:

```csharp
// Does NOT compile — AIAgent is not assignable to Func<IServiceProvider, IChatClient>.
agent.ChatClient = sp => new HarnessAgent(innerChatClient, options);
```

### The pipeline slot rejects it too

`ConfigureAgentPipeline` is an `Action<AIAgentBuilder>`, so it composes middleware that *wraps* this
library's inner agent. `HarnessAgent` cannot: its only constructor takes an `IChatClient` and builds
its own `ChatClientAgent` underneath. There is no way to hand it the inner agent to delegate to.

MAF decorators that *are* plain wrappers do compose here — `OpenTelemetryAgent` is supported and its
disposal is handled by the pipeline lease.

### And its tool loop is the thing this library replaces

Even setting the type system aside, `FunctionInvokingChatClient` sits at the bottom of
`HarnessAgent`'s chat pipeline and runs the **entire tool-call loop in process**. This library
rejects that client anywhere in a chat chain, throwing `DurableFunctionInvocationConflictException`
— see [design-decisions.md](../../design-decisions.md).

The rejection is the whole point of the library. Each tool call is dispatched as its own
`InvokeAgentTool` Temporal activity, so it is independently retried, timed out, and recorded in
workflow history. Collapse that into one in-process loop and a crash mid-loop re-runs every tool in
the batch — including write-style tools that `NoRetry()` exists to protect. Per-tool timeouts, retry
policy, and Web UI visibility all disappear with it.

---

## The individual providers

The context providers listed above ship in **core `Microsoft.Agents.AI`**, which this library
already references — you do not need the Harness package to reach them. `HarnessAgent` enables them;
it does not own them.

Registering one directly with `agent.AddContextProvider(...)` is supported *only* when it meets the
[bounded durable `ChatClientAgent` contract](../../architecture/MAF/bounded-durable-agent-compatibility.md).
Most of these do not, because they expose tools dynamically through `AIContext.Tools`, which this
library never dispatches. See [individual-context-providers.md](./individual-context-providers.md)
for the supported pattern and the three ways to give a provider's tools durable execution.

### `BackgroundAgentsProvider` can never work

It stores live `Task<AgentResponse>` handles in its runtime state. Those are in-process objects:
not serializable, meaningless after continue-as-new, and unrecoverable on a worker restart —
`AgentWorkflow` continues-as-new once history passes `MaxEntryCount`.

Use `WorkflowAgents.ExecuteAgentsInParallelAsync` for fan-out. It reaches the same result through
`Workflow.WhenAllAsync` and is replay-safe.

### `ToolApprovalAgent` — use this library's approval instead

`ToolApprovalAgent` mediates approval at the *agent boundary*: it surfaces
`ToolApprovalRequestContent` to the caller and reads `ToolApprovalResponseContent` back out of
caller messages. In this library the workflow gates tool dispatch before any activity is scheduled,
so there is no caller round-trip at that boundary to carry the exchange.

Its state is not the obstacle — `ToolApprovalState` persists in the session's
`AgentSessionStateBag`, and outer pipeline middleware here receives the real, restored
`TemporalAgentSession`. (`null` is passed only at the innermost boundary, to the library-created
`ChatClientAgent`, which needs its own transient `ChatClientAgentSession`.) The mismatch is in where
the approval conversation happens, not in whether state survives.

Use `RequireApproval()`, an `IAgentToolInterceptor` returning `PauseForApproval(...)`, or in-tool
`RequestApprovalAsync`. See [hitl-patterns.md](./hitl-patterns.md).

> Composing `ToolApprovalAgent` as pipeline middleware is untested in this repository.

---

## See also

- [individual-context-providers.md](./individual-context-providers.md) — the supported provider pattern
- [hitl-patterns.md](./hitl-patterns.md) — durable approval
- [usage.md](./usage.md) — full agent registration API
