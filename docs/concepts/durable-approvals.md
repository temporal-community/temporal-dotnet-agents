# Durable approvals

Durable approvals let a reviewer resolve a tool call after its workflow has parked. The pending request is workflow state, so no activity worker is occupied during a workflow-parked wait and the request survives worker restarts.

The normative authentication, resource-authorization, payload, and effect-time rules are in the
[security boundary](../security.md).

## One-call decisions

Both libraries expose the shared `DurableApprovalRequest`, `DurableApprovalDecision`, and `DurableApprovalResolutionResult` contracts. Use the library-specific typed client so the application retains ownership of resource lookup:

```csharp
var pending = await agentClient.GetPendingApprovalAsync(sessionId, cancellationToken);
if (pending is not null)
{
    var result = await agentClient.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
            Reason = "Reviewed by the operations team.",
        },
        cancellationToken);
}
```

MEAI uses its conversation ID; MAF uses `TemporalAgentSessionId`. The libraries intentionally do not expose a shared raw-workflow-ID control interface. A Temporal workflow ID or conversation ID is a routing locator, not proof that the caller may view or approve that resource.

Resolution is retry-safe:

- `Accepted`: the pending request was resolved;
- `AlreadyResolved`: an equivalent decision was retained after an ambiguous client result;
- `Conflict`: the same request ID was previously resolved differently;
- `RequestMismatch`: another request is pending;
- `NotPending`: no matching pending or retained request exists.

The most recent 32 decisions are retained across Continue-As-New. A timeout resolves the request as rejected.

## Invalid requests

`RequestApproval` requires a non-empty, non-whitespace `RequestId`. A malformed request fails that
Update terminally with the stable error type `DurableApprovalInvalidRequest`; it does not create a
pending approval, schedule an activity, or poison the workflow for later valid turns. Treat this as
an application/request bug rather than retrying the same payload.

## MAF reusable session grants

Reusable approval is a separate privileged capability. It is not available on `ITemporalAgentClient.ResolveApprovalAsync`, which always decides one call. A trusted administrative backend may explicitly register and use:

```csharp
services.AddTemporalAgentApprovalScopeAdministration();

var grant = await scopeAdministration.GrantSessionScopeAsync(
    sessionId,
    new SessionApprovalScopeGrantRequest
    {
        RequestId = pending.RequestId,
        Pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Exact,
            Parameter = "accountId",
            Pattern = authorizedAccountId,
        },
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        Actor = reviewerDisplayName,
        Reason = "Ticket INC-1234",
    },
    cancellationToken);
```

Exactly one constraint is required: a specific `Pattern`, or explicit `MatchAllArguments = true`. Every grant expires, belongs to one workflow session, has a stable `GrantId`, is bounded in workflow state, and can be revoked with `RevokeSessionScopeAsync`. Cross-session and permanent grants are intentionally unsupported.

`Actor` and `Reason` are untrusted audit strings; the package does not authenticate them. Before querying or resolving an approval, the host application must authenticate the caller and authorize the application resource that maps to the typed conversation/session ID. A matching approval grant is also not effect-time authorization: activities that perform external effects must re-read authoritative tenant, ownership, and authorization data immediately before the effect.
