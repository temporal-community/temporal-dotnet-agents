# MAF `HarnessAgent` Compatibility

MAF's `HarnessAgent` is a batteries-included pre-wired agent that bundles a chat client, function invocation loop, todo management, file memory, agent-mode switching, background agent delegation, and approval gating into a single object. **It is structurally incompatible with `TemporalCommunity.Extensions.Agents`** and cannot be used as a drop-in replacement for a plain `IChatClient`.

This document explains the three independent blockers and what to use instead.

---

## Why it doesn't work

### 1. `FunctionInvocationDelegatingAgent` collapses the tool loop into one activity

`HarnessAgent` wraps its inner agent with `FunctionInvocationDelegatingAgent`, which runs the **entire tool-call loop inside a single `RunAsync` call**. Every tool call — `todos_add`, `mode_set`, a file read, a shell command — executes sequentially within that one invocation.

This library dispatches each tool call as a **separate** `InvokeAgentTool` Temporal activity via `Workflow.WhenAllAsync`. That is the central durability guarantee: each tool is independently retried, timed out, and recorded in workflow history. If the worker crashes between two tool calls, only the incomplete tool re-runs — the completed ones replay from history.

Wrapping `HarnessAgent` would collapse all tool calls into a single activity boundary. A crash mid-loop loses all tools that had not yet completed; on retry the whole batch re-runs. Write-style tools (send email, apply refund) would re-execute — exactly the foot-gun `NoRetry()` exists to prevent. Per-tool observability in the Temporal Web UI disappears. Per-tool timeout and retry configuration become inoperable.

### 2. `BackgroundAgentsProvider` holds live `Task<T>` references

`BackgroundAgentsProvider` stores live `Task<AgentResponse>` objects in its runtime state to track background agent invocations. These are in-process handles — they are not serializable and cannot survive a continue-as-new transition or a worker restart.

`AgentWorkflow` triggers continue-as-new when history grows past `MaxEntryCount`. On continue-as-new, the workflow state (including `CarriedStateBag`) is restarted in a new execution. Any `Task<T>` reference stored in the provider's state has no meaning in the new execution context.

The correct pattern for parallel agent fan-out is `WorkflowAgents.ExecuteAgentsInParallelAsync`, which uses `Workflow.WhenAllAsync` — fully durable and replay-safe across worker restarts and continue-as-new boundaries.

### 3. `ToolApprovalAgent` loses approval session state

`HarnessAgent` wraps the inner agent with `ToolApprovalAgent`, which tracks approval state keyed on the `AgentSession` instance. When this library dispatches tool calls it passes `session: null` to the MAF agent internals. `ToolApprovalAgent` never receives the session key it needs to look up approval decisions, so approval state is permanently lost.

For durable HITL approval gates, use `TemporalAgentContext.Current.RequestApprovalAsync` from inside a tool activity. See [`hitl-patterns.md`](./hitl-patterns.md).

---

## Why you cannot plug it in accidentally

`DurableAgentBuilder.ChatClient` requires a `Func<IServiceProvider, IChatClient>` — a factory that returns an `IChatClient`. `HarnessAgent` (and all `AIAgent` subclasses) does not implement `IChatClient`. The .NET type system rejects the assignment at compile time:

```csharp
// Does NOT compile — AIAgent is not assignable to Func<IServiceProvider, IChatClient>.
agent.ChatClient = sp => new HarnessAgent(...);
```

If you obtained an `IChatClient` adapter from a `HarnessAgent` through reflection or casting, you would reach the runtime blockers above rather than a compile error — but the standard path is a build failure with a clear type mismatch message.

---

## What to use instead

Do not treat individual Harness providers as automatic escape hatches. Most of them expose tools dynamically or depend on Harness-owned orchestration, so they are not supported unchanged. A provider is compatible only when it meets the [bounded durable `ChatClientAgent` contract](../../architecture/MAF/bounded-durable-agent-compatibility.md); static tools need explicit durable declarations through `IDurableToolSource` or `AddContextProvider(provider, durableTools)`.

See [`individual-context-providers.md`](./individual-context-providers.md) for the supported provider pattern and exclusions.

For the full agent registration API, see [`usage.md`](./usage.md).
