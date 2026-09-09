# MAF `HarnessAgent` compatibility

**Short answer: you cannot use `HarnessAgent` with this library.** Of the two slots you might try,
one rejects it at compile time and the other compiles but is rejected at worker startup.

`HarnessAgent` ships in **`Microsoft.Agents.AI.Harness`**, a package separate from the
`Microsoft.Agents.AI` this library depends on. Version `1.17.0` exists there, matching the MAF
version pinned in `Directory.Packages.props`. This library does not reference it.

Verified against the `Microsoft.Agents.AI.Harness` 1.17.0 and `Microsoft.Agents.AI` 1.17.0 packages,
with both code samples below compiled against them.

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

### The pipeline slot compiles, then fails at startup

`ConfigureAgentPipeline` is an `Action<AIAgentBuilder>`, and `AIAgentBuilder.Use(Func<AIAgent,
AIAgent>)` accepts a factory that ignores the inner agent it is handed. So this **does** compile:

```csharp
// Compiles. Rejected when the worker starts.
agent.ConfigureAgentPipeline = b => b.Use(_ => new HarnessAgent(chatClient, harnessOptions));
```

`HarnessAgent`'s only constructor takes an `IChatClient` and builds its own `ChatClientAgent`
underneath, so there is no way to hand it the library's inner agent to delegate to — the factory can
only discard it. Every configured pipeline is dry-built during startup validation, and a factory
that does not preserve the library-created inner agent is rejected with
`DurableConfigurationException`:

> Agent '{name}' has a ConfigureAgentPipeline factory that removed or hid the library-created inner
> agent. Every custom wrapper must derive from DelegatingAIAgent and pass the factory's supplied
> inner agent to its base constructor. […]

MAF decorators that *are* transparent `DelegatingAIAgent` wrappers do compose here —
`OpenTelemetryAgent` is supported and its disposal is handled by the pipeline lease.

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
**All six fail it**, because each exposes tools dynamically through `AIContext.Tools`, which this
library never dispatches — `TodoProvider`, `AgentModeProvider`, `FileMemoryProvider`, and
`FileAccessProvider` each publish their own tool set, `AgentSkillsProvider` publishes `load_skill`,
`read_skill_resource`, and `run_skill_script`, and `BackgroundAgentsProvider` publishes six. See
[individual-context-providers.md](./individual-context-providers.md) for the supported pattern and
the three ways to give a provider's tools durable execution.

For skills specifically, this library ships its own durable equivalent: `agent.UseSkills(...)`
registers skill tools as ordinary durable tools. See [skills.md](./skills.md).

### `BackgroundAgentsProvider` can never work

It keeps in-flight work in `BackgroundAgentRuntimeState`, which MAF documents as holding
"non-serializable runtime references" — its `Task<AgentResponse>` and `AgentSession` properties are
`[JsonIgnore]`d precisely because they cannot be serialized.

MAF handles that loss deliberately rather than crashing: after deserialization it builds a fresh
empty runtime state and marks every previously-running task `BackgroundTaskStatus.Lost`. That is
survivable in a long-lived process. It is not survivable here, because `AgentWorkflow`
continues-as-new every time history reaches `MaxEntryCount` — so in a durable session the provider
would quietly mark in-flight delegated work `Lost` at each transition, losing it without an error.

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
