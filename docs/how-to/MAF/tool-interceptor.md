# Tool Interceptor (`IAgentToolInterceptor`)

A pre-tool lifecycle hook that runs as a Temporal activity before each `InvokeAgentTool` dispatch. Use it to apply policy, enrich approval context, score risk, scrub PII from arguments, or short-circuit tool execution — all without modifying individual tool implementations.

> **Looking to intercept the *model* call instead** — logging prompts and responses, token usage, or wrapping the agent run? That is a different layer: see [Intercepting LLM Calls](./llm-call-interception.md).

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
9. [Batch fan-out and approval behavior](#batch-fan-out-and-approval-behavior)
10. [Approval ownership](#approval-ownership)
11. [Implementation examples](#implementation-examples)

---

## What it is

When an agent turn produces tool calls, the workflow dispatches a `RunToolInterceptor` activity for each one — in parallel, before dispatching any `InvokeAgentTool` activity. The tool activities themselves then run serially in model order. The interceptor runs as a proper Temporal activity, so it can do I/O: database lookups, external API calls, risk scoring services. Its result is recorded in workflow history. On replay, the activity is not re-executed.

This is different from the in-tool HITL path (`TemporalAgentContext.Current.RequestApprovalAsync`), which fires after a tool has already started. The interceptor fires before the tool activity is dispatched at all.

For a comparison of the two approval flavors, see [HITL Patterns](./hitl-patterns.md).

---

## The interface

```csharp
using TemporalCommunity.Extensions.AI.Tools;    // DurableToolDecision, IDurableToolInterceptor
using TemporalCommunity.Extensions.Agents.Tools; // IAgentToolInterceptor, AgentToolContext

public interface IAgentToolInterceptor : IDurableToolInterceptor<AgentToolContext>
{
    Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);
}
```

`BeforeToolCallAsync` is called once per tool call per turn. Return a `DurableToolDecision` to control what happens next.

> **Library split:** `DurableToolDecision`, `DurableToolContext`, and `IDurableToolInterceptor<TContext>` are
> defined in `TemporalCommunity.Extensions.AI.Tools`. `IAgentToolInterceptor` and `AgentToolContext` live in
> `TemporalCommunity.Extensions.Agents.Tools`. Implementors need `using TemporalCommunity.Extensions.AI.Tools;` for the decision type.

The interface has exactly one method, `BeforeToolCallAsync`. Interception is a pre-dispatch gate:
a tool's result cannot be inspected or rewritten here. Use
[`TemporalAgentOptions.ToolMiddleware`](llm-call-interception.md#tool-middleware) for a MAF-shaped
callback inside the tool activity, after required approval, including short-circuit and result
transformation. It cannot change the approved call. Both activities can retry their own effects.

---

## AgentToolContext

```csharp
// DurableToolContext (TemporalCommunity.Extensions.AI.Tools) — cross-library base
public class DurableToolContext
{
    public required string ToolName { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
    public string? CallId { get; init; }
    public string? SessionId { get; init; }

    // Declared on the type, but NOT populated by either library's interception path today.
    // They read null in an interceptor; do not branch on them.
    public string? ConversationId { get; init; }
    public string? CorrelationId { get; init; }
    public int? TurnNumber { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

// AgentToolContext (TemporalCommunity.Extensions.Agents.Tools) — MAF-specific extension
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
| `StateBag` | Snapshot immediately before tool dispatch. Successful interceptor write-backs are merged into workflow-owned state before the next activity. Keep mutations retry-safe; a failed or cancelled turn restores the pre-turn StateBag, but cannot undo external effects. `null` when no state has accumulated. |

---

## The four decision outcomes

Use the static factory methods on `DurableToolDecision` (from `TemporalCommunity.Extensions.AI.Tools`) to construct instances.

### `Proceed`

Dispatch the tool normally.

```csharp
using TemporalCommunity.Extensions.AI.Tools;

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

The `description` string is shown to the reviewer via `DurableApprovalRequest.Description`. The external system polls `GetPendingApprovalAsync` and calls retry-safe `ResolveApprovalAsync` to unblock the workflow — the same API used by the in-tool HITL path.

If approved, the tool proceeds. If rejected, the tool is skipped and the agent receives a synthetic rejection result.

For reviewer-side polling, resolution statuses, and operational constraints, see
[HITL Patterns — Workflow-parked approval](./hitl-patterns.md#workflow-parked-approval).

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
    "delete_records",
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<DataService>().DeleteRecords,
        name: "delete_records"),
    opts => opts.RequireApproval());
```

When both `RequireApproval()` and an interceptor are active, the interceptor's `EnrichedDescription` (from a `Proceed` decision) is used as the approval request description. This lets the interceptor contribute context — for example, a risk score or entity summary — to the reviewer's approval UI even when the gate itself is unconditional.

---

## Registration

Register an interceptor per agent or set a worker-level default.

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
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

            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder, name: "lookup_order"));

            agent.AddTool(
                "cancel_order",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<OrderService>().CancelOrder, name: "cancel_order"),
                opts => opts.WithTimeout(TimeSpan.FromSeconds(30)));
        });
    });
```

The H1 rule applies: a per-agent `AddToolInterceptor` registration wins over `opts.DefaultToolInterceptor`. There is no stacking — only one interceptor fires per tool call.

---

## Per-tool opt-out

Opt a specific tool out of the interceptor with `SkipInterceptor()`:

```csharp
agent.AddTool(
    "search_products",
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<CatalogService>().SearchProducts, name: "search_products"),
    opts => opts.SkipInterceptor());
```

When `SkipInterceptor()` is set, the `RunToolInterceptor` activity is not dispatched for that tool. The tool proceeds directly to `InvokeAgentTool`.

Use this for read-only, low-risk tools where the interceptor overhead (an extra activity round-trip per tool call) buys nothing. Avoid it for write tools or any tool that handles sensitive data.

---

## Interceptor activity timeout

By default, the `RunToolInterceptor` activity uses the per-agent `ActivityTimeout` (falling back to the worker-level `DefaultActivityTimeout` when the agent doesn't set one). Override it per tool with an independent budget:

```csharp
agent.AddTool(
    "write_record",
    sp => AIFunctionFactory.Create(
        sp.GetRequiredService<DataService>().WriteRecord, name: "write_record"),
    opts => opts
        .WithInterceptorTimeout(TimeSpan.FromSeconds(10)));  // interceptor gets 10s
        // tool's own StartToCloseTimeout still falls through agent.ActivityTimeout
        // to opts.DefaultActivityTimeout
```

`WithInterceptorTimeout` sets only the `RunToolInterceptor` activity's `StartToCloseTimeout`. It is independent of `WithTimeout`, which governs the `InvokeAgentTool` activity.

---

## Batch fan-out and approval behavior

All `RunToolInterceptor` activities for a turn fan out in parallel. Tool dispatch waits until every
interceptor result is recorded. For the resulting batch-safety guarantee, timeout implications, and
the difference from approval requested inside a running tool, see
[Human-in-the-loop patterns](./hitl-patterns.md#what-usually-decides-it).

---

## Approval ownership

Workflow-parked approval state belongs to the registered `AgentWorkflow`. A regular workflow that
needs approval-capable agent execution invokes its registered proxy through an activity; it does not
run the agent locally. See [HITL patterns](./hitl-patterns.md).

---

## Implementation examples

### Context enrichment for approval description

Load order details from the database so the reviewer sees meaningful context rather than raw LLM arguments.

```csharp
using TemporalCommunity.Extensions.AI.Tools;     // DurableToolDecision
using TemporalCommunity.Extensions.Agents.Tools; // IAgentToolInterceptor, AgentToolContext

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
using TemporalCommunity.Extensions.AI.Tools;     // DurableToolDecision
using TemporalCommunity.Extensions.Agents.Tools; // IAgentToolInterceptor, AgentToolContext

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
using TemporalCommunity.Extensions.AI.Tools;     // DurableToolDecision
using TemporalCommunity.Extensions.Agents.Tools; // IAgentToolInterceptor, AgentToolContext

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

The tool receives the token instead of the raw SSN. `ModifiedArguments` affects only the `ActivityScheduled(InvokeAgentTool)` event. The LLM's original arguments appear in two earlier events: `ActivityCompleted(RunDurableAgentStep)` (the LLM step result) and `ActivityScheduled(RunToolInterceptor)` (the interceptor's input). This library does not provide a path that removes those event-history payloads; apply your Temporal data-protection controls where that is required.

---

## See also

- [Durable Agents](./durable-agents.md) — per-tool activity configuration, `DurableToolOptions` reference
- [HITL Patterns](./hitl-patterns.md) — reviewer-side approval flow, `ResolveApprovalAsync`, and approval-pattern comparison
- [Usage Guide — Tool Activity Ordering and Retry Policy](./usage.md#tool-activity-ordering-and-retry-policy)
- [`samples/MAF/ToolInterceptor/`](../../../samples/MAF/ToolInterceptor/) — runnable sample: all four decision paths + `RequireApproval()` in a refund-agent scenario

---

_Last updated: 2026-09-09_
