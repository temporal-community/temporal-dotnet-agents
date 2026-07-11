# Human-in-the-Loop Patterns

How to implement approval gates, build approval dashboards, handle timeouts, and test HITL flows in TemporalAgents.

---

## Table of Contents

1. [Overview](#overview)
2. [How Approval Works](#how-approval-works)
3. [Requesting Approval from a Tool](#requesting-approval-from-a-tool)
4. [Building an Approval Dashboard](#building-an-approval-dashboard)
5. [Timeout Configuration](#timeout-configuration)
6. [Multi-Step Approval Chains](#multi-step-approval-chains)
7. [Error Handling and Rejection](#error-handling-and-rejection)
8. [Workflow-Parked Approval (Feature A)](#workflow-parked-approval-feature-a)
9. [Approval Scopes (Feature B)](#approval-scopes-feature-b)
10. [Testing HITL Flows](#testing-hitl-flows)
11. [Types Reference](#types-reference)
12. [Complete Example: Email Approval](#complete-example-email-approval)

---

## Overview

TemporalAgents supports two HITL flavors. Choose based on how long the approval window is and whether the tool has work to do mid-gate:

| Flavor | How triggered | Activity pinned? | Best for |
|--------|---------------|------------------|---------|
| **In-tool** (`RequestApprovalAsync`) | Explicit call inside a tool implementation | Yes — activity heartbeats during the wait | Short, interactive approvals (seconds to minutes); gates mid-tool logic |
| **Workflow-parked** (`RequireApproval()` / `PauseForApproval`) | `DurableToolOptions.RequireApproval()` or `IAgentToolInterceptor` returning `PauseForApproval` | No — turn loop parks; no activity pinned | Multi-day waits; cost-sensitive workloads; approval before any tool work begins |

Both flavors use `SubmitApprovalAsync` for external systems to unblock the workflow.

Three `[WorkflowUpdate]` / `[WorkflowQuery]` handlers make this work:

| Handler | Type | Purpose |
|---------|------|---------|
| `RequestApprovalAsync` | Update | Called from inside a tool; blocks until human responds |
| `SubmitApprovalAsync` | Update | Called from external system; unblocks the tool |
| `GetPendingApproval` | Query | Called from external system; polls for pending requests |

---

## How Approval Works

```
Agent Tool                    AgentWorkflow                  External System
    │                              │                              │
    │  RequestApprovalAsync        │                              │
    │─────────────────────────────>│                              │
    │                              │  stores _pendingApproval     │
    │                              │  blocks on WaitConditionAsync│
    │  (activity suspended)        │                              │
    │                              │                              │
    │                              │  GetPendingApproval (query)  │
    │                              │<─────────────────────────────│
    │                              │──────────────────────────────>│
    │                              │  returns DurableApprovalRequest │
    │                              │                              │
    │                              │  SubmitApprovalAsync (update)│
    │                              │<─────────────────────────────│
    │                              │  sets _approvalDecision      │
    │                              │  WaitConditionAsync unblocks │
    │                              │                              │
    │  DurableApprovalDecision returned │                         │
    │<─────────────────────────────│                              │
    │                              │                              │
    │  tool continues or cancels   │                              │
```

The key insight: `RequestApprovalAsync` is a `[WorkflowUpdate]` called from inside the activity (via `TemporalAgentContext`). The workflow blocks on `WaitConditionAsync` while the activity remains suspended. The activity heartbeats during this period, so the worker won't treat it as stuck.

---

## Requesting Approval from a Tool

Call `TemporalAgentContext.Current.RequestApprovalAsync` from inside any agent tool implementation:

```csharp
var sendEmailTool = AIFunctionFactory.Create(
    async (
        [Description("Recipient email")] string to,
        [Description("Email subject")]   string subject,
        [Description("Email body")]      string body) =>
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Send email to {to} — Subject: {subject}\n\nBody:\n{body}"
            });

        if (!decision.Approved)
        {
            return $"Email rejected by reviewer: {decision.Reason ?? "no reason given"}";
        }

        // Proceed with the actual action
        await SendEmailAsync(to, subject, body);
        return $"Email sent to {to}.";
    },
    name: "send_email",
    description: "Sends an email. Requires human approval.");
```

**Important:** The tool function is `async` and awaits the approval decision. The entire `proxy.RunAsync` call that triggered this tool remains suspended until the human responds (or the timeout elapses).

---

## Building an Approval Dashboard

The external system (UI, CLI, monitoring service) uses two methods on `ITemporalAgentClient`:

### Polling for Pending Approvals

`GetPendingApprovalAsync` is a `[WorkflowQuery]` — it's read-only, never blocks the workflow, and is safe to call at any frequency:

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("EmailAssistant", userId);

// Poll until an approval appears
DurableApprovalRequest? pending = await client.GetPendingApprovalAsync(sessionId);

if (pending is not null)
{
    Console.WriteLine($"Description: {pending.Description}");
    Console.WriteLine($"Request ID: {pending.RequestId}");
}
```

### Submitting a Decision

`SubmitApprovalAsync` is a `[WorkflowUpdate]` — it validates the `RequestId`, sets the decision, and unblocks the tool:

```csharp
await client.SubmitApprovalAsync(
    sessionId,
    new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Reviewed and approved by operations team."
    });
```

### Validation Guards

The workflow validates submissions before they enter history:

- **No pending request:** throws `InvalidOperationException` — "No approval request is pending"
- **Wrong RequestId:** throws `InvalidOperationException` — "Decision RequestId does not match pending request"

These guards prevent stale or misrouted decisions from affecting the workflow.

### Polling Pattern for a Console App

The `HumanInTheLoop` sample demonstrates a polling loop that stays responsive while the agent is suspended:

```csharp
// Start the agent call (may block inside a tool)
var agentTask = proxy.RunAsync(userMessages, session);

while (!agentTask.IsCompleted)
{
    await Task.Delay(TimeSpan.FromSeconds(1));

    DurableApprovalRequest? pending = null;
    try
    {
        pending = await client.GetPendingApprovalAsync(sessionId);
    }
    catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
    {
        continue; // Workflow not started yet
    }

    if (pending is null) continue;

    // Display the request and collect human input
    var approved = PromptForDecision(pending);

    await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = approved,
        Reason    = approved ? null : "Rejected by reviewer."
    });
}

var response = await agentTask; // Agent resumes and returns final response
```

---

## Timeout Configuration

### ApprovalTimeout

Controls how long the workflow waits for a human response before auto-rejecting:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.DefaultApprovalTimeout = TimeSpan.FromHours(4); // default: 7 days
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

When the timeout elapses, `RequestApprovalAsync` returns a rejected decision:

```csharp
new DurableApprovalDecision
{
    RequestId = request.RequestId,
    Approved  = false,
    Reason    = "Approval timed out after 4 hours with no human response."
}
```

### ActivityTimeout

The activity that hosts the tool **also** has a timeout. It must exceed the `ApprovalTimeout`, otherwise the activity times out before the human can respond:

```csharp
opts.DefaultActivityTimeout    = TimeSpan.FromHours(24); // must exceed ApprovalTimeout
opts.DefaultHeartbeatTimeout   = TimeSpan.FromMinutes(5);
opts.DefaultApprovalTimeout    = TimeSpan.FromHours(4);
```

**Rule of thumb:** `ActivityTimeout` > `ApprovalTimeout` + expected LLM processing time.

### Continue-as-New

`ApprovalTimeout` survives continue-as-new transitions — it's carried forward in `AgentWorkflowInput.ApprovalTimeout`.

---

## Multi-Step Approval Chains

A single tool can request multiple approvals sequentially:

```csharp
var deleteTool = AIFunctionFactory.Create(async (string userId) =>
{
    // First gate: data deletion
    var decision1 = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = $"Delete user data for {userId} — This will remove all records. Irreversible."
        });

    if (!decision1.Approved)
        return $"Data deletion rejected: {decision1.Reason}";

    // Second gate: account deactivation
    var decision2 = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = $"Deactivate account for {userId} — User will lose access immediately."
        });

    if (!decision2.Approved)
        return $"Account deactivation rejected: {decision2.Reason}. Data was still deleted.";

    await DeleteAndDeactivateAsync(userId);
    return $"User {userId} data deleted and account deactivated.";
},
name: "delete_user",
description: "Deletes user data and deactivates account. Requires two approvals.");
```

Each `RequestApprovalAsync` call is a separate `[WorkflowUpdate]` round-trip. The external system sees each pending request individually and can approve/reject them independently.

**Caveat:** If the first approval is granted but the second is rejected, the first action may have already been performed (depending on your tool logic). Design tools so that partial completion is either safe or explicitly handled.

---

## Error Handling and Rejection

### Handling Rejection in Tools

```csharp
var decision = await TemporalAgentContext.Current.RequestApprovalAsync(request);

if (!decision.Approved)
{
    // Option 1: Return a message — agent incorporates it into its response
    return $"Action rejected: {decision.Reason ?? "no reason given"}";

    // Option 2: Throw — the tool fails and the agent reports an error
    throw new OperationCanceledException($"Rejected: {decision.Reason}");
}
```

Returning a message is generally preferred — it lets the agent explain the rejection to the user and offer alternatives.

### Handling Timeout in Tools

Timeout produces the same rejected ticket, so the tool handles it identically:

```csharp
if (!decision.Approved)
{
    // Could be a human rejection or a timeout
    var reason = decision.Reason ?? "unknown reason";
    return $"Action not approved: {reason}";
}
```

The `Reason` field distinguishes the two cases — timeouts include "timed out" in the message.

### Handling Submission Errors

`SubmitApprovalAsync` can throw `InvalidOperationException` if:
- No approval is pending (the tool hasn't called `RequestApprovalAsync` yet)
- The `RequestId` doesn't match the pending request

Handle these in the external system:

```csharp
try
{
    await client.SubmitApprovalAsync(sessionId, decision);
}
catch (InvalidOperationException ex)
{
    // Stale request — the approval may have timed out
    Console.WriteLine($"Cannot submit: {ex.Message}");
}
```

---

## Workflow-Parked Approval (Feature A)

The in-tool path (`RequestApprovalAsync`) keeps an activity running while waiting for a human — the activity heartbeats during the pause. This is the right choice for interactive approvals (seconds to minutes) where the tool has logic to execute after the gate.

For multi-day approval windows or cost-sensitive workloads where pinning an activity slot is undesirable, use the **workflow-parked** flavor: the turn loop itself parks, no activity is pinned, and the workflow resumes only after `SubmitApprovalAsync` is called.

### Triggering Workflow-Parked Approval

Two mechanisms trigger the workflow-parked flavor:

**1. `DurableToolOptions.RequireApproval()` — absolute floor**

Set on any tool registration. Approval is always required before the tool runs, even if the `IAgentToolInterceptor` returns `Proceed`:

```csharp
opts.AddDurableAgent("DataAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

    agent.AddTool(
        sp => AIFunctionFactory.Create(
            sp.GetRequiredService<DataService>().DeleteRecords,
            "delete_records"),
        opts => opts.RequireApproval());  // always park for human approval
});
```

**2. `IAgentToolInterceptor` returning `PauseForApproval`**

Register an interceptor that evaluates each tool call and decides dynamically whether to pause:

```csharp
using TemporalCommunity.Extensions.AI.Tools;     // DurableToolDecision
using TemporalCommunity.Extensions.Agents.Tools; // IAgentToolInterceptor, AgentToolContext

public class RiskyToolInterceptor : IAgentToolInterceptor
{
    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context, CancellationToken cancellationToken)
    {
        // Park for approval on any destructive tool call
        if (context.ToolName.StartsWith("delete_") || context.ToolName.StartsWith("send_"))
        {
            return Task.FromResult(DurableToolDecision.PauseForApproval(
                $"Tool '{context.ToolName}' requires human approval. Arguments: {context.Arguments}"));
        }

        return Task.FromResult(DurableToolDecision.Proceed());
    }
}

// Register on a specific agent:
opts.AddDurableAgent("DataAgent", agent =>
{
    agent.AddToolInterceptor(sp => new RiskyToolInterceptor());
    // ...
});

// Or as a worker-level default (applies to all agents that don't override):
opts.DefaultToolInterceptor = sp => new RiskyToolInterceptor();
```

> **Rename only:** Returning `DurableToolDecision.PauseForApproval(description)` from an interceptor is a rename
> of the former `AgentToolDecision.PauseForApproval` — the approval flow is identical. The `description` still
> becomes `DurableApprovalRequest.Description` on the reviewer's side. `SubmitApprovalAsync`,
> `GetPendingApprovalAsync`, `DurableApprovalRequest`, and `DurableApprovalDecision` are all unchanged;
> only the interceptor outcome type name changed (it now lives in `TemporalCommunity.Extensions.AI.Tools`).

> **Note:** `PauseForApproval` is only supported on `AgentWorkflow`-backed agents (sessions and sub-agents inside workflows). On `AgentJobWorkflow` (`AddScheduledAgentRun`, `ScheduleAgentAsync`) the decision degrades to `Block` with a warning logged, because scheduled jobs have no persistent session to resume.

### Unblocking a Parked Workflow

The external system calls `SubmitApprovalAsync` exactly as in the in-tool path — the API surface is identical:

```csharp
await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
{
    RequestId = pending.RequestId,
    Approved  = true,
    Reason    = "Approved after compliance review."
});
```

If approved, the tool executes normally. If rejected, the tool is skipped and the agent receives a synthetic rejection result.

### When to Use Which Flavor

| | In-tool (`RequestApprovalAsync`) | Workflow-parked (`RequireApproval()` / `PauseForApproval`) |
|---|---|---|
| Activity pinned while waiting | Yes | No |
| Suitable for multi-day waits | With a large `ActivityTimeout` | Yes — no timeout concern |
| Gate positioned | Mid-tool logic | Pre-tool dispatch |
| Interceptor integration | N/A | Native |
| `AgentJobWorkflow` support | Yes | No — degrades to `Block` |

---

## Approval Scopes (Feature B)

Approval scopes let a reviewer's decision carry forward automatically so the same tool does not require human review on every subsequent call. Instead of blocking for approval each time, the workflow consults a scope record written when a previous approval was granted and proceeds without interruption.

Three scope lifetimes are available:

| `ApprovalScope` | Lifetime | Storage |
|---|---|---|
| `ThisCallOnly` | Single call — no record written (default) | — |
| `Session` | Remainder of the current session; survives `continue-as-new` | `AgentSessionStateBag` |
| `Always` | Across all sessions for this agent | External `IApprovalScopeStore` |

### Registering a scope-aware tool

Opt a tool into scope-aware auto-approval with `.RequireApproval().ScopeAware()` and call `UseApprovalScopes()` on the agent builder:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("FileAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    ([Description("Path to write")] string path) => $"Wrote {path}",
                    new AIFunctionFactoryOptions { Name = "write_file" }),
                opts => opts.RequireApproval().ScopeAware());

            // Installs the built-in ScopedApprovalInterceptor for this agent.
            agent.UseApprovalScopes();
        });
    });
```

Without `.ScopeAware()`, a `RequireApproval()` tool always pauses for human review regardless of previously granted scopes. Without `UseApprovalScopes()`, the `.ScopeAware()` flag has no effect (no interceptor consults scope records) and worker startup throws `InvalidOperationException`.

### Submitting a decision with a scope

The reviewer calls `SubmitApprovalAsync` with the desired `Scope` field populated:

```csharp
await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
{
    RequestId = pending.RequestId,
    Approved  = true,
    Scope     = ApprovalScope.Session,   // or ApprovalScope.Always
});
```

When `Scope = ThisCallOnly` (the default when the field is omitted), no scope record is written and the next call to the same tool will pause again.

### Scoping by argument pattern

To scope approval to a specific argument value rather than the entire tool, set `ScopePattern` on the decision:

```csharp
await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
{
    RequestId    = pending.RequestId,
    Approved     = true,
    Scope        = ApprovalScope.Session,
    ScopePattern = new ApprovalScopePattern
    {
        Type      = PatternMatchType.Glob,
        Parameter = "path",
        Pattern   = "/tmp/*",
    },
});
```

With this decision, only future calls to `write_file` where the `path` argument matches `/tmp/*` are auto-approved. Calls with `/prod/*` paths still pause for review. When `ScopePattern` is `null`, the scope covers any call to the named tool regardless of arguments.

`PatternMatchType` values:

| Value | Behavior |
|---|---|
| `Exact` | Exact string comparison (`Ordinal`) |
| `Glob` | `*` matches any sequence except `/`; `**` matches including `/` |
| `Regex` | .NET `Regex.IsMatch` with a 100 ms timeout (ReDoS protection) |

### Configuring `IApprovalScopeStore` for always-scope persistence

`ApprovalScope.Always` requires an external store to persist scope records across sessions. Configure it at the worker level or per agent:

```csharp
// Worker-level default — applies to every UseApprovalScopes() agent unless overridden.
opts.ApprovalScopeStore = sp => new MyApprovalScopeStore(
    sp.GetRequiredService<IDbConnection>());

// Or per-agent inside the builder:
agent.UseApprovalScopes(scopes =>
{
    scopes.ApprovalScopeStore = sp => new MyApprovalScopeStore(
        sp.GetRequiredService<IDbConnection>());
});
```

`IApprovalScopeStore` (namespace `TemporalCommunity.Extensions.Agents.Approvals`) has two methods:

```csharp
Task<IReadOnlyList<ApprovalScopeRecord>> LoadAsync(
    string agentName, string storeKey, CancellationToken ct = default);

Task AppendAsync(
    string agentName, string storeKey, ApprovalScopeRecord record,
    CancellationToken ct = default);
```

Both are called from Temporal activities — Temporal's built-in retry policy handles transient failures. `AppendAsync` must be idempotent: if a record with the same `OriginatingRequestId` already exists for the agent/key pair, the operation is a no-op.

When no store is configured, `ApprovalScope.Always` degrades to `ApprovalScope.Session` and a `LogWarning` is emitted. The scope record is written to StateBag instead and lost when the session ends.

### Always-scope cache

At the start of each workflow run (and after `continue-as-new`), the workflow dispatches a `LoadAlwaysScopes` activity to read persisted records from the store and cache them in StateBag. This means:

- Always-scope lookups during the turn loop are in-memory (no store I/O per tool call).
- New always-scopes written during a session become visible immediately via StateBag; they are also visible to fresh sessions that load the store at startup.

The cache is bounded by `ApprovalScopesOptions.MaxAlwaysScopeCacheRecords` (default: 256) and `MaxAlwaysScopeCacheBytes` (default: 32 KiB). Records beyond the budget are not cached; the first 256 records (by store order) are cached and the rest are dropped from the in-memory view.

### Customizing scope options per agent

```csharp
agent.UseApprovalScopes(scopes =>
{
    // Key under which always-scopes are stored and loaded.
    // Must not equal "temporal.approval_scopes.session" (reserved).
    scopes.AlwaysScopesStoreKey = "temporal.approval_scopes.always";   // default

    // Disable auto-loading at session start (manage via your own UI/policy).
    scopes.ApplyAlwaysScopesAtSessionStart = false;

    // Budget limits for the always-scope StateBag cache.
    scopes.MaxAlwaysScopeCacheRecords = 128;
    scopes.MaxAlwaysScopeCacheBytes   = 16 * 1024;

    // Timeouts for the LoadAlwaysScopes / AppendAlwaysScope activities.
    scopes.ApprovalScopeActivityTimeout     = TimeSpan.FromSeconds(10);
    scopes.ApprovalScopeActivityMaximumAttempts = 2;
});
```

### Degradation on unsupported execution paths

`ApprovalScope.Session` and `ApprovalScope.Always` require a persistent session — the workflow must have an active `AgentWorkflow` instance where scope records can be stored.

| Execution path | Scope behavior |
|---|---|
| `TemporalAIAgentProxy` → `AgentWorkflow` | Full support: Session and Always scopes work as documented |
| `AgentJobWorkflow` (scheduled jobs, `ScheduleAgentAsync`) | Session scope: `LogWarning`, record dropped. Always scope: `LogWarning`, record not persisted (store call skipped) |
| `TemporalAIAgent` (sub-agent via `GetTemporalAgent()`) | Same as `AgentJobWorkflow` — no persistent session |

For scheduled jobs and sub-agents, configure `RequireApproval()` tools without `.ScopeAware()` if you do not want silent scope-record failures.

---

## Testing HITL Flows

### Integration Tests

The test suite covers both the timeout path and the happy path:

```csharp
[Fact]
public async Task RequestApproval_TimesOut_ReturnsRejectedDecision()
{
    // Build a custom host with a short approval timeout
    var host = BuildHostWithApprovalTimeout(TimeSpan.FromSeconds(2));
    await host.StartAsync();

    // Start workflow and send the approval request
    var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(workflowId);
    var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.RequestApprovalAsync(new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = "Test action — Test details"
        }));

    Assert.False(decision.Approved);
    Assert.Contains("timed out", decision.Reason);
}

[Fact]
public async Task SubmitApproval_BeforeTimeout_ReturnsApprovedDecision()
{
    // Use a longer timeout so we can submit before it elapses
    var host = BuildHostWithApprovalTimeout(TimeSpan.FromMinutes(5));
    await host.StartAsync();

    // Request approval in background
    var approvalTask = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.RequestApprovalAsync(request));

    // Wait for the request to be pending, then submit
    await Task.Delay(500);
    await handle.ExecuteUpdateAsync(
        wf => wf.SubmitApprovalAsync(new DurableApprovalDecision
        {
            RequestId = request.RequestId,
            Approved  = true
        }));

    var decision = await approvalTask;
    Assert.True(decision.Approved);
}
```

See [Testing Agents](./testing-agents.md) for the full integration test fixture pattern.

---

## Types Reference

### DurableApprovalRequest

```csharp
// Namespace: TemporalCommunity.Extensions.AI.Approvals
public sealed record DurableApprovalRequest
{
    public required string RequestId { get; init; }            // must be set explicitly, e.g. Guid.NewGuid().ToString("N")
    public string? FunctionName { get; init; }                 // optional: name of the tool requesting approval
    public string? CallId { get; init; }                       // optional: tool call correlation ID
    public string? Description { get; init; }                  // human-readable description for the reviewer
}
```

> **Note:** In MAF HITL flows, `FunctionName` and `CallId` are always `null`. These fields are populated by `DurableAIFunction` in MEAI tool-call flows (`TemporalCommunity.Extensions.AI`), which are not part of the MAF pipeline. When building a shared approval UI that handles requests from both libraries, check these fields for null before displaying them. Tool authors should always populate `RequestId` and `Description` — these are the fields a human reviewer will see.

### DurableApprovalDecision

```csharp
// Namespace: TemporalCommunity.Extensions.AI.Approvals
// Used for both the submitted decision and the returned outcome
public sealed record DurableApprovalDecision
{
    public string RequestId { get; init; } = string.Empty;     // must match pending request
    public bool Approved { get; init; }
    public string? Reason { get; init; }                       // reviewer note or timeout message
    public ApprovalScope Scope { get; init; }                  // default: ThisCallOnly (no scope record written)
    public ApprovalScopePattern? ScopePattern { get; init; }   // optional argument-level pattern constraint
}
```

`Scope` and `ScopePattern` are serialized with `JsonIgnoreCondition.WhenWritingDefault` / `WhenWritingNull`, so legacy JSON without these fields deserializes as `ThisCallOnly` with no pattern — no breaking change for existing approval integrations.

### ApprovalScopePattern

```csharp
// Namespace: TemporalCommunity.Extensions.AI.Approvals
public sealed class ApprovalScopePattern
{
    public PatternMatchType Type { get; init; }    // Exact | Glob | Regex
    public required string Pattern { get; init; }
    public string? Parameter { get; init; }        // null = match against full argument JSON
}
```

### ApprovalScope

```csharp
// Namespace: TemporalCommunity.Extensions.AI.Approvals
public enum ApprovalScope
{
    ThisCallOnly,   // default — no scope record written
    Session,        // scoped to the current session; survives continue-as-new
    Always,         // persisted to IApprovalScopeStore; applies across all sessions
}
```

---

## Complete Example: Email Approval

The `samples/MAF/HumanInTheLoop/` sample implements a full email assistant with HITL approval. Key components:

**Tool definition** — `send_email` pauses for approval before sending:

```csharp
var sendEmailTool = AIFunctionFactory.Create(
    async (string to, string subject, string body) =>
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Send email to {to} — Subject: {subject}\n\nBody:\n{body}"
            });

        if (!decision.Approved)
            return $"Email to {to} was rejected ({decision.Reason ?? "no reason"}).";

        // Send the email
        return $"Email sent to {to}.";
    },
    name: "send_email",
    description: "Sends an email. Requires human approval.");
```

**Worker configuration** — 24-hour activity timeout for human review:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services
    .AddHostedTemporalWorker("hitl-sample")
    .AddTemporalAgents(opts =>
    {
        opts.DefaultActivityTimeout  = TimeSpan.FromHours(24);
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(5);

        opts.AddDurableAgent("EmailAgent", agent =>
        {
            agent.Instructions = "You are an email assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(sendEmailTool);
            agent.TimeToLive   = TimeSpan.FromHours(2);
        });
    });
```

**Console loop** — polls for approvals and collects human decisions:

```csharp
var agentTask = proxy.RunAsync(userMessages, session);

while (!agentTask.IsCompleted)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    var pending = await client.GetPendingApprovalAsync(sessionId);
    if (pending is null) continue;

    // Display approval request and prompt for decision
    var approved = choice == "approve";
    await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = approved,
        Reason    = approved ? null : reason
    });
}

var response = await agentTask;
```

Run it with:

```bash
dotnet run --project samples/MAF/HumanInTheLoop
```

---

## References

- `src/TemporalCommunity.Extensions.AI/Approvals/DurableApprovalRequest.cs` — request type
- `src/TemporalCommunity.Extensions.AI/Approvals/DurableApprovalDecision.cs` — decision and outcome type (includes `Scope`, `ScopePattern`)
- `src/TemporalCommunity.Extensions.AI/Approvals/ApprovalScope.cs` — `ApprovalScope` enum
- `src/TemporalCommunity.Extensions.AI/Approvals/ApprovalScopePattern.cs` — `ApprovalScopePattern` and `PatternMatchType`
- `src/TemporalCommunity.Extensions.Agents/Approvals/ApprovalScopeRecord.cs` — persisted scope record
- `src/TemporalCommunity.Extensions.Agents/Approvals/ApprovalScopeHelpers.cs` — `TryMatchScope` public helper
- `src/TemporalCommunity.Extensions.Agents/Approvals/ApprovalScopesOptions.cs` — per-agent scope configuration
- `src/TemporalCommunity.Extensions.Agents/Approvals/IApprovalScopeStore.cs` — always-scope store interface
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentWorkflow.cs` — HITL update/query handlers, scope record write path
- `src/TemporalCommunity.Extensions.Agents/Session/TemporalAgentContext.cs` — `RequestApprovalAsync` for tools
- `samples/MAF/HumanInTheLoop/` — complete working example
- [Tool Interceptor — Approval scope records](./tool-interceptor.md#approval-scope-records) — interceptor integration, one-turn lag
- [Usage Guide — HITL](./usage.md#human-in-the-loop-hitl-approval-gates) — quick-start examples
- [Testing Agents](./testing-agents.md) — integration test patterns

---

_Last updated: 2026-06-05_
