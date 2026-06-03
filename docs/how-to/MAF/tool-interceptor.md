# Tool Interceptor (`IAgentToolInterceptor`)

A pre-tool lifecycle hook that runs as a Temporal activity before each `InvokeAgentTool` dispatch. Use it to apply policy, enrich approval context, score risk, scrub PII from arguments, or short-circuit tool execution — all without modifying individual tool implementations.

---

## Table of Contents

1. [What it is](#what-it-is)
2. [The interface](#the-interface)
3. [AgentToolContext](#agenttoolcontext)
4. [The four decision outcomes](#the-four-decision-outcomes)
5. [RequireApproval — the configuration-time floor](#requireapproval--the-configuration-time-floor)
6. [Registration](#registration)
7. [Per-tool opt-out](#per-tool-opt-out)
8. [Interceptor activity timeout](#interceptor-activity-timeout)
9. [Batch fan-out and safety guarantee](#batch-fan-out-and-safety-guarantee)
10. [PauseForApproval on scheduled and sub-agent paths](#pauseforapproval-on-scheduled-and-sub-agent-paths)
11. [Implementation examples](#implementation-examples)

---

## What it is

When an agent turn produces tool calls, the workflow dispatches a `RunToolInterceptor` activity for each one — in parallel, before dispatching any `InvokeAgentTool` activity. The interceptor runs as a proper Temporal activity, so it can do I/O: database lookups, external API calls, risk scoring services. Its result is recorded in workflow history. On replay, the activity is not re-executed.

This is different from the in-tool HITL path (`TemporalAgentContext.Current.RequestApprovalAsync`), which fires after a tool has already started. The interceptor fires before the tool activity is dispatched at all.

For a comparison of the two approval flavors, see [HITL Patterns](./hitl-patterns.md).

---

## The interface

```csharp
using Temporalio.Extensions.AI;    // DurableToolDecision, IDurableToolInterceptor
using Temporalio.Extensions.Agents; // IAgentToolInterceptor, AgentToolContext

public interface IAgentToolInterceptor : IDurableToolInterceptor<AgentToolContext>
{
    Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);
}
```

`BeforeToolCallAsync` is called once per tool call per turn. Return a `DurableToolDecision` to control what happens next.

> **Library split:** `DurableToolDecision`, `DurableToolContext`, and `IDurableToolInterceptor<TContext>` are
> defined in `Temporalio.Extensions.AI`. `IAgentToolInterceptor` and `AgentToolContext` remain in
> `Temporalio.Extensions.Agents`. Implementors need `using Temporalio.Extensions.AI;` for the decision type.

`AfterToolCallAsync` is named and reserved for a follow-on release. When it ships, the interface will add a default implementation so existing interceptors are not broken.

---

## AgentToolContext

```csharp
// DurableToolContext (Temporalio.Extensions.AI) — cross-library base
public class DurableToolContext
{
    public required string ToolName { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
    public string? CallId { get; init; }
    public string? SessionId { get; init; }
    // + ConversationId, CorrelationId, TurnNumber, Metadata (Phase 2)
}

// AgentToolContext (Temporalio.Extensions.Agents) — MAF-specific extension
public sealed class AgentToolContext : DurableToolContext
{
    public required string AgentName { get; init; }
    public AgentSessionStateBag? StateBag { get; init; }
}
```

| Property | Notes |
|---|---|
| `AgentName` | The registered agent name. |
| `ToolName` | The tool name as registered on the agent. |
| `Arguments` | The argument dictionary the LLM emitted in its `FunctionCallContent`. |
| `CallId` | LLM-assigned call identifier. `null` for models that do not emit call IDs. |
| `StateBag` | Snapshot of session state after the most recent LLM step completed, immediately before tool dispatch. Deserialized in the interceptor activity from the same source as `TemporalAgentSession.FromStateBag`. Treat as read-only — mutations to the object are not persisted back to the workflow; only the LLM-step activity's `UpdatedStateBag` flows back. `null` when no state has accumulated. |

---

## The four decision outcomes

Use the static factory methods on `DurableToolDecision` (from `Temporalio.Extensions.AI`) to construct instances.

### `Proceed`

Dispatch the tool normally.

```csharp
using Temporalio.Extensions.AI;

return DurableToolDecision.Proceed(
    enrichedDescription: $"Looked up order #{orderId} — value $240.00",
    metadata: new Dictionary<string, string> { ["risk_score"] = "0.1" });
```

Optional parameters:

- `enrichedDescription` — human-readable context injected into `DurableApprovalRequest.Description` when the tool has `RequireApproval()` set (the `Proceed` decision is overridden to `PauseForApproval` by the Rule 2 floor, using this description for the reviewer).
- `modifiedArguments` — replacement argument dictionary. The turn loop passes these to `InvokeAgentToolInput` instead of the original LLM-supplied arguments. The LLM's original arguments are already in Temporal history from `RunDurableAgentStep`; this substitution only affects the tool dispatch event. Use this for PII scrubbing (see [PII argument scrubbing](#pii-argument-scrubbing)).
- `metadata` — arbitrary key/value pairs stored in Temporal history for audit purposes.

### `PauseForApproval`

Park the turn loop and wait for a human to approve before the tool runs.

```csharp
return DurableToolDecision.PauseForApproval(
    $"Tool '{context.ToolName}' requested on agent '{context.AgentName}'. " +
    $"Arguments: {JsonSerializer.Serialize(context.Arguments)}");
```

The `description` string is shown to the reviewer via `DurableApprovalRequest.Description`. The external system polls `GetPendingApprovalAsync` and calls `SubmitApprovalAsync` to unblock the workflow — the same API used by the in-tool HITL path.

If approved, the tool proceeds. If rejected, the tool is skipped and the agent receives a synthetic rejection result.

For the full approval dashboard API and testing patterns, see [HITL Patterns — Workflow-Parked Approval](./hitl-patterns.md#workflow-parked-approval-feature-a).

### `Skip`

Do not dispatch the tool. Inject `syntheticResult` as a `FunctionResultContent` so the LLM receives a well-formed tool result without the activity executing.

```csharp
return DurableToolDecision.Skip(
    "Order ORD-999 not found. No action taken.",
    metadata: new Dictionary<string, string> { ["reason"] = "order_not_found" });
```

Use this for pre-flight validation failures where the LLM should receive a plausible "no-op" result and continue. The LLM sees the synthetic result as a normal tool response.

### `Block`

Do not dispatch the tool. Inject an error result carrying `reason` so the LLM is informed the call was blocked.

```csharp
return DurableToolDecision.Block(
    "Policy violation: bulk_delete is not permitted during off-hours.",
    metadata: new Dictionary<string, string> { ["policy"] = "off-hours-write-block" });
```

Use this for guardrail violations, compliance policy failures, or any case where the correct signal to the LLM is an error rather than a plausible result.

---

## RequireApproval — the configuration-time floor

`DurableToolOptions.RequireApproval()` is an absolute floor set at registration time. Even if the interceptor returns `Proceed`, the tool still waits for human approval before dispatching.

```csharp
agent.AddTool(
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<DataService>().DeleteRecords,
        "delete_records"),
    opts => opts.RequireApproval());
```

When both `RequireApproval()` and an interceptor are active, the interceptor's `EnrichedDescription` (from a `Proceed` decision) is used as the approval request description. This lets the interceptor contribute context — for example, a risk score or entity summary — to the reviewer's approval UI even when the gate itself is unconditional.

---

## Registration

Register an interceptor per agent or set a worker-level default.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        // Worker-level default — applies to every agent that does not register its own.
        opts.DefaultToolInterceptor = sp => new RiskScoringInterceptor(
            sp.GetRequiredService<RiskService>());

        opts.AddDurableAgent("OrderAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            // Per-agent interceptor — overrides the worker default for this agent only.
            agent.AddToolInterceptor(sp => new OrderPolicyInterceptor(
                sp.GetRequiredService<OrderPolicyService>()));

            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder, "lookup_order"));

            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<OrderService>().CancelOrder, "cancel_order"),
                opts => opts.NoRetry());
        });
    });
```

The H1 rule applies: a per-agent `AddToolInterceptor` registration wins over `opts.DefaultToolInterceptor`. There is no stacking — only one interceptor fires per tool call.

---

## Per-tool opt-out

Opt a specific tool out of the interceptor with `SkipInterceptor()`:

```csharp
agent.AddTool(
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<CatalogService>().SearchProducts, "search_products"),
    opts => opts.SkipInterceptor());
```

When `SkipInterceptor()` is set, the `RunToolInterceptor` activity is not dispatched for that tool. The tool proceeds directly to `InvokeAgentTool`.

Use this for read-only, low-risk tools where the interceptor overhead (an extra activity round-trip per tool call) buys nothing. Avoid it for write tools or any tool that handles sensitive data.

---

## Interceptor activity timeout

By default, the `RunToolInterceptor` activity uses the per-agent `ActivityTimeout` (falling back to the worker-level `DefaultActivityTimeout` when the agent doesn't set one). Override it per tool with an independent budget:

```csharp
agent.AddTool(
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<DataService>().WriteRecord, "write_record"),
    opts => opts
        .NoRetry()
        .WithInterceptorTimeout(TimeSpan.FromSeconds(10)));  // interceptor gets 10s
        // tool's own StartToCloseTimeout still inherits the worker default
```

`WithInterceptorTimeout` sets only the `RunToolInterceptor` activity's `StartToCloseTimeout`. It is independent of `WithTimeout`, which governs the `InvokeAgentTool` activity.

---

## Batch fan-out and safety guarantee

All `RunToolInterceptor` activities for a given turn fan out in parallel via `Workflow.WhenAllAsync`. No `InvokeAgentTool` activity is dispatched until every interceptor result for that turn is recorded in history.

This matters for write tools. If a turn fans out three tool calls and one of them requires human approval, the approval gate parks the entire turn before any of the three tools executes. The workflow does not start two tools while waiting for approval on the third — that ordering guarantee is built into the two-phase structure of each turn.

---

## PauseForApproval on scheduled and sub-agent paths

`PauseForApproval` requires a workflow with a persistent session — it relies on the `[WorkflowUpdate]` handlers on `AgentWorkflow` to park and resume the turn loop.

On `AgentJobWorkflow` (the workflow backing `AddScheduledAgentRun` and `ScheduleAgentAsync`) and on `TemporalAIAgent` (workflow-context sub-agents accessed via `GetAgent()`), neither has the approval mixin. If an interceptor returns `PauseForApproval` on these paths, the decision degrades automatically to `Block` and a warning is logged. The tool is not dispatched and the LLM receives a block error result.

If an interceptor may return `PauseForApproval`, use it only with session-backed agents (`TemporalAIAgentProxy` → `AgentWorkflow`). For scheduled jobs and sub-agents, prefer `Skip` or `Block` for policy enforcement.

---

## Implementation examples

### Context enrichment for approval description

Load order details from the database so the reviewer sees meaningful context rather than raw LLM arguments.

```csharp
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents;

public class OrderApprovalInterceptor(OrderRepository repo) : IAgentToolInterceptor
{
    public async Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext ctx, CancellationToken ct)
    {
        if (ctx.ToolName is not "cancel_order")
            return DurableToolDecision.Proceed();

        var orderId = ctx.Arguments.TryGetValue("orderId", out var id) ? id?.ToString() : null;
        if (orderId is null)
            return DurableToolDecision.Skip("Missing orderId argument.");

        var order = await repo.GetAsync(orderId, ct);
        if (order is null)
            return DurableToolDecision.Skip($"Order {orderId} not found.");

        return DurableToolDecision.PauseForApproval(
            $"Cancel order {orderId} — customer {order.CustomerName}, " +
            $"total ${order.Total:F2}, placed {order.PlacedAt:d}.");
    }
}
```

### Risk scoring / guardrail

Call an external risk API and block if the score exceeds a threshold.

```csharp
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents;

public class RiskScoringInterceptor(IRiskService riskService) : IAgentToolInterceptor
{
    private const double BlockThreshold = 0.8;

    public async Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext ctx, CancellationToken ct)
    {
        var score = await riskService.ScoreAsync(ctx.AgentName, ctx.ToolName, ctx.Arguments, ct);

        if (score >= BlockThreshold)
            return DurableToolDecision.Block(
                $"Tool '{ctx.ToolName}' blocked by risk policy (score {score:F2} >= {BlockThreshold}).",
                metadata: new Dictionary<string, string> { ["risk_score"] = $"{score:F2}" });

        return DurableToolDecision.Proceed(
            metadata: new Dictionary<string, string> { ["risk_score"] = $"{score:F2}" });
    }
}
```

### PII argument scrubbing

Tokenize a sensitive field before it reaches the tool, keeping PII out of the `InvokeAgentTool` activity event.

```csharp
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents;

public class PiiScrubbingInterceptor(IPiiVault vault) : IAgentToolInterceptor
{
    public async Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext ctx, CancellationToken ct)
    {
        if (!ctx.Arguments.TryGetValue("ssn", out var raw) || raw is null)
            return DurableToolDecision.Proceed();

        var token = await vault.TokenizeAsync(raw.ToString()!, ct);

        var scrubbed = new Dictionary<string, object?>(ctx.Arguments)
        {
            ["ssn"] = token,
        };

        return DurableToolDecision.Proceed(modifiedArguments: scrubbed);
    }
}
```

The tool receives the token instead of the raw SSN. `ModifiedArguments` affects only the `ActivityScheduled(InvokeAgentTool)` event. The LLM's original arguments appear in two earlier events: `ActivityCompleted(RunDurableAgentStep)` (the LLM step result) and `ActivityScheduled(RunToolInterceptor)` (the interceptor's input). For complete Temporal history PII isolation, use the `IAgentHistoryStore` external-store path.

---

## See also

- [Durable Agents](./durable-agents.md) — per-tool activity configuration, `DurableToolOptions` reference
- [HITL Patterns](./hitl-patterns.md) — approval dashboard API, `SubmitApprovalAsync`, in-tool vs workflow-parked comparison
- [Usage Guide — Per-Tool Activity Configuration](./usage.md#per-tool-activity-configuration)
- [`samples/MAF/ToolInterceptor/`](../../../samples/MAF/ToolInterceptor/) — runnable sample: all four decision paths + `RequireApproval()` in a refund-agent scenario

---

_Last updated: 2026-06-02_
