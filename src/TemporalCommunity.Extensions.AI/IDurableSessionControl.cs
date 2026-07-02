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
    /// Submits a human decision for a pending tool approval request, unblocking the workflow.
    /// </summary>
    /// <param name="workflowId">The raw Temporal workflow ID for the session.</param>
    /// <param name="decision">The approval or rejection decision.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SubmitApprovalAsync(
        string workflowId,
        DurableApprovalDecision decision,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels the pending approval request (if any) by submitting a rejected decision on
    /// behalf of an external system. No-op when no approval is pending.
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

        await SubmitApprovalAsync(
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
