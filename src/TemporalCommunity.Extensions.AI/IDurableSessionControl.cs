using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Shared HITL and lifecycle surface common to both the MEAI and MAF session clients.
/// Approval dashboards and ops tooling can take a single <see cref="IDurableSessionControl"/>
/// dependency and work against either library without coupling to a specific client type.
/// </summary>
/// <remarks>
/// Implementations in this SDK:
/// <list type="bullet">
/// <item><see cref="DurableChatSessionClient"/> (MEAI / <c>TemporalCommunity.Extensions.AI</c>)</item>
/// <item><c>DefaultTemporalAgentClient</c> (MAF / <c>TemporalCommunity.Extensions.Agents</c>)</item>
/// </list>
/// The <paramref name="workflowId"/> parameter is the raw Temporal workflow ID (not a
/// conversation ID or session key). Callers can obtain it via the client's own
/// <c>GetWorkflowId</c> / <c>SessionId.WorkflowId</c> helpers.
/// For MEAI applications, prefer <see cref="IDurableChatSessionClient.ResolveApprovalAsync"/>,
/// which returns retry-safe resolution status. This raw-workflow-ID surface provides the same
/// retry-safe generic resolution contract to shared approval tooling for either library.
/// </remarks>
public interface IDurableSessionControl
{
    /// <summary>
    /// Returns the currently pending tool approval request for the session identified by
    /// <paramref name="workflowId"/>, or <see langword="null"/> if no approval is pending.
    /// </summary>
    /// <param name="workflowId">The raw Temporal workflow ID for the session.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        string workflowId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a human decision for a pending tool approval request and returns a retry-safe
    /// status. An identical retry after a lost response returns <c>AlreadyResolved</c>; a
    /// different decision for the same request returns <c>Conflict</c>.
    /// </summary>
    /// <param name="workflowId">The raw Temporal workflow ID for the session.</param>
    /// <param name="decision">The approval or rejection decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
        string workflowId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the pending approval request (if any) by resolving it as rejected on behalf of
    /// an external system. No-op when no approval is pending. An <c>AlreadyResolved</c> result
    /// is intentionally ignored because another reviewer already completed the request.
    /// </summary>
    /// <param name="workflowId">The raw Temporal workflow ID for the session.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task CancelPendingApprovalAsync(
        string workflowId,
        CancellationToken ct = default)
    {
        var pending = await GetPendingApprovalAsync(workflowId, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return;
        }

        _ = await ResolveApprovalAsync(
            workflowId,
            new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                Approved = false,
                Reason = "Cancelled externally.",
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a graceful shutdown signal to the session workflow so it exits its session loop
    /// rather than sitting parked until its configured time-to-live expires.
    /// </summary>
    /// <param name="workflowId">The raw Temporal workflow ID for the session.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ShutdownAsync(string workflowId, CancellationToken ct = default);
}
