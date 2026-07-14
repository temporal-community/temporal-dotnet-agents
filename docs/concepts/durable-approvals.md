# Durable approvals

Durable approvals let a reviewer resolve a tool call after the workflow has paused. The
pending request is workflow state, so it survives worker restarts and is available to a
dashboard through a workflow query.

## Shared approval lifecycle

1. A tool or interceptor creates a `DurableApprovalRequest`.
2. A reviewer reads it with `GetPendingApprovalAsync`.
3. The reviewer calls `ResolveApprovalAsync` with a request ID and a decision.
4. The workflow resumes when the result status is `Accepted`.

`ResolveApprovalAsync` is safe to retry. It returns `AlreadyResolved` when the same
decision is delivered again after an ambiguous client response, and `Conflict` when a
retry changes the decision. `RequestMismatch` means another request is pending;
`NotPending` means there is neither a pending request nor a retained resolved request
with that ID.

Each workflow execution chain retains the most recent 32 resolved approvals. The
retained records are carried through continue-as-new. A duplicate outside that window
returns `NotPending`, rather than being assumed to be successful.

Approval timeouts resolve the pending request as rejected. Callers should handle that
returned rejection just as they handle a reviewer rejection.

## MEAI: generic, per-request decisions

`TemporalCommunity.Extensions.AI` exposes the generic wire contract:

```csharp
var result = await sessionClient.ResolveApprovalAsync(conversationId,
    new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved = true,
        Reason = "Approved by the on-call reviewer.",
    });
```

MEAI decisions apply only to the current request. They never grant reusable approval
for later tool calls.

## MAF: optional reusable scopes

`TemporalCommunity.Extensions.Agents` adds `DurableAgentApprovalDecision` for agent
workflows. It includes the same core fields and can additionally grant an
`ApprovalScope`:

```csharp
var result = await agentClient.ResolveApprovalAsync(sessionId,
    new DurableAgentApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved = true,
        Scope = ApprovalScope.Session,
    });
```

`ThisCallOnly` is the default. `Session` and `Always` scopes are available only for
MAF tools configured for approval scopes. See [MAF HITL patterns](../how-to/MAF/hitl-patterns.md)
for configuration and persistence details.

## Shared dashboards

An approval dashboard that intentionally does not grant MAF scopes can depend on
`IDurableSessionControl` and use a raw workflow ID:

```csharp
async Task ResolveFromDashboardAsync(
    IDurableSessionControl control,
    string workflowId,
    DurableApprovalRequest pending,
    CancellationToken cancellationToken)
{
    var result = await control.ResolveApprovalAsync(workflowId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
        },
        cancellationToken);

    if (result.Status is not (DurableApprovalResolutionStatus.Accepted or
        DurableApprovalResolutionStatus.AlreadyResolved))
    {
        throw new InvalidOperationException($"Approval was not applied: {result.Status}.");
    }
}
```

This generic path applies `ThisCallOnly` when it targets an MAF workflow; it cannot
grant `Session` or `Always` scope. Use `ITemporalAgentClient.ResolveApprovalAsync` and
`DurableAgentApprovalDecision` when a reviewer is allowed to grant those scopes.
