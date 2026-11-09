using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Client for running agents via Temporal workflow updates.
/// </summary>
public interface ITemporalAgentClient
{
    /// <summary>
    /// Sends a request to an agent session and waits for the response.
    /// Starts the workflow if it is not already running.
    /// </summary>
    Task<AgentResponse> SendAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the active or retained outcome for a request without submitting or resubmitting it.
    /// </summary>
    /// <remarks>
    /// The workflow retains only a bounded number and size of terminal outcomes. An unavailable
    /// result means the workflow or request outcome is not available; it does not prove that the
    /// request was never accepted. Do not automatically resubmit on that basis.
    /// </remarks>
    Task<TemporalAgentTurnOutcome> GetTurnOutcomeAsync(
        TemporalAgentSessionId sessionId,
        string requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an agent by submitting a validated fire-and-forget update.
    /// Starts the workflow if it is not already running.
    /// Returns after observing the queueing update's outcome, without waiting for the agent
    /// response. Admission failures are propagated to the caller.
    /// </summary>
    Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries current Temporal event-history usage and the session's configured admission thresholds.
    /// </summary>
    Task<DurableChatHistoryBudgetStatus> GetHistoryBudgetAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries the configured retained-history reducer for a blocked agent session once.
    /// Use a distinct, persisted operation ID for each retry attempt.
    /// </summary>
    Task<DurableHistoryRecoveryResult> RetryHistoryCompactionAsync(
        TemporalAgentSessionId sessionId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly trims an oldest complete-turn prefix once for the current blocked incident.
    /// Removed history is represented by a truncation marker.
    /// </summary>
    Task<DurableHistoryRecoveryResult> TrimBlockedHistoryAsync(
        TemporalAgentSessionId sessionId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests workflow-owned cancellation of the active turn identified by
    /// <paramref name="requestId"/>.
    /// </summary>
    /// <param name="sessionId">The application-owned agent session identifier.</param>
    /// <param name="requestId">The stable request ID supplied when the turn was started.</param>
    /// <param name="cancellationToken">
    /// Cancellation token for this RPC wait only. It does not cancel an accepted turn.
    /// </param>
    Task<DurableTurnCancellationResult> CancelTurnAsync(
        TemporalAgentSessionId sessionId,
        string requestId,
        CancellationToken cancellationToken = default);

    // ── Human-in-the-Loop (GAP 3) ────────────────────────────────────────────

    /// <summary>
    /// Queries the agent workflow for a pending <see cref="DurableApprovalRequest"/>,
    /// returning <see langword="null"/> if none exists.
    /// </summary>
    Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one matching pending agent-tool approval. Unknown, mismatched, already-resolved,
    /// or conflicting decisions fail update validation before Temporal records an accepted update.
    /// Tool approvals require both <see cref="DurableApprovalDecision.CallId"/> and
    /// <see cref="DurableApprovalDecision.FunctionName"/> from the pending request.
    /// After an ambiguous RPC result, reconcile with application-owned review state rather than
    /// retrying the decision blindly.
    /// </summary>
    Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
        TemporalAgentSessionId sessionId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the pending approval request (if any) by resolving it as rejected on behalf of
    /// an external system. No-op when no approval is pending; an already-resolved request is
    /// intentionally ignored.
    /// </summary>
    /// <param name="sessionId">The agent session ID.</param>
    /// <param name="reason">Optional reason recorded on the rejection. Defaults to <c>"Cancelled externally."</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task CancelPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingApprovalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return;
        }

        _ = await ResolveApprovalAsync(
            sessionId,
            new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                CallId = pending.CallId,
                FunctionName = pending.FunctionName,
                Approved = false,
                Reason = reason ?? "Cancelled externally.",
            },
            cancellationToken).ConfigureAwait(false);
    }

    
    /// <summary>
    /// Sends a validated graceful-shutdown update to the agent session workflow, causing it to exit its
    /// session loop rather than sitting parked until the configured <c>TimeToLive</c>.
    /// </summary>
    /// <param name="sessionId">The agent session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Equivalent to executing the <c>RequestShutdown</c> update on the workflow handle directly,
    /// but keeps the caller free of the raw workflow ID and update name details.
    /// </remarks>
    Task ShutdownAsync(TemporalAgentSessionId sessionId, CancellationToken cancellationToken = default);
}
