# Durable approvals

Durable approvals let a reviewer resolve a tool call after its workflow has parked. The pending request is workflow state, so no activity worker is occupied during a workflow-parked wait and the request survives worker restarts.

The normative authentication, resource-authorization, payload, and effect-time rules are in the
[security boundary](../security.md).

**This page is the contract.** For choosing between the two MAF approval shapes, wiring a reviewer,
and the operational constraints of each, see
[MAF human-in-the-loop patterns](../how-to/MAF/hitl-patterns.md); the MEAI equivalent is
[MEAI HITL patterns](../how-to/MEAI/hitl-patterns.md).

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

Only a matching pending decision is accepted. `ResolveApprovalAsync` returns `Accepted` when the
decision is applied; unknown, mismatched, already-resolved, or conflicting decisions fail update
validation before Temporal records an accepted update. The result/status enum also names those
non-progressing cases for internal classification, but callers should handle the update failure
rather than expect an accepted result for them.

The most recent 32 decisions are retained across Continue-As-New. A timeout resolves the request as rejected.
If an update response is ambiguous, inspect the pending request and reconcile it with application-owned
review state; do not assume blindly retrying the decision will return the original result.

Reviewer metadata is empty by default: model-supplied tool arguments are not copied into
`ReviewData`. Applications may opt in to a reviewer-safe description and metadata. The combined
serialized JSON projection is limited to 16 KiB and at most 64 metadata entries; requests exceeding
either bound fail non-retryably before acceptance. Descriptions remain caller-authored and can
contain model-influenced text, so sanitize them for the reviewer surface.

## Invalid requests

`RequestApproval` requires a non-empty, non-whitespace `RequestId`. A malformed request fails that
Update terminally with the stable error type `DurableApprovalInvalidRequest`; it does not create a
pending approval, schedule an activity, or poison the workflow for later valid turns. Treat this as
an application/request bug rather than retrying the same payload.

Before querying or resolving an approval, the host application must authenticate the caller and
authorize the application resource that maps to the typed conversation/session ID. Approval is
not effect-time authorization: activities that perform external effects must re-read authoritative
tenant, ownership, and authorization data immediately before the effect.
