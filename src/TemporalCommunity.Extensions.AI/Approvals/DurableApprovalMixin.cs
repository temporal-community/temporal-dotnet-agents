using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Internal helper that encapsulates the shared HITL approval state machine.
/// Both <c>DurableChatWorkflow</c> and <c>AgentWorkflow</c> hold an instance of this class
/// and delegate their approval-related update and query methods to it.
/// </summary>
/// <remarks>
/// This class is not a workflow — it carries no <c>[Workflow]</c> attribute.
/// The host workflow is responsible for keeping its own <c>[WorkflowUpdate]</c>,
/// <c>[WorkflowUpdateValidator]</c>, and <c>[WorkflowQuery]</c> attribute declarations so
/// Temporal can discover the handlers. The methods here are thin implementations that
/// the workflow delegates to.
///
/// <b>Temporal constraint:</b> this class calls <c>Workflow.WaitConditionAsync</c> from within
/// <see cref="RequestApprovalAsync"/>. It must therefore only ever be called from workflow-thread
/// code. Because the class is <c>internal</c> that is always the case.
/// </remarks>
internal sealed class DurableApprovalMixin
{
    private const int MaxRetainedResolutions = 32;
    private DurableApprovalRequest? _pendingApproval;
    private DurableApprovalDecision? _approvalDecision;
    private readonly List<DurableApprovalDecision> _resolvedApprovals = [];

    /// <summary>
    /// Restores the bounded resolution history carried into a new workflow run.
    /// </summary>
    public void RestoreResolvedApprovals(IReadOnlyList<DurableApprovalDecision>? decisions)
    {
        _resolvedApprovals.Clear();
        if (decisions is null)
        {
            return;
        }

        foreach (var decision in decisions.TakeLast(MaxRetainedResolutions))
        {
            _resolvedApprovals.Add(decision);
        }
    }

    /// <summary>
    /// Returns the bounded resolution history for continue-as-new input.
    /// </summary>
    public IReadOnlyList<DurableApprovalDecision> GetResolvedApprovals() => _resolvedApprovals;

    // ── Validators ──────────────────────────────────────────────────────────

    /// <summary>
    /// Validates an incoming <see cref="DurableApprovalRequest"/> before it enters workflow history.
    /// Throws <see cref="InvalidOperationException"/> when a request is already pending, matching
    /// the exception type used by both callers prior to this extraction.
    /// </summary>
    public void ValidateRequestApproval(DurableApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.RequestId))
            throw new ArgumentException("RequestId must not be null or empty.");
        if (_pendingApproval is not null)
            throw new InvalidOperationException(
                "An approval request is already pending. Submit or timeout the current request before sending another.");
    }

    /// <summary>
    /// Validates an incoming <see cref="DurableApprovalDecision"/> before it enters workflow history.
    /// Throws <see cref="InvalidOperationException"/> when no request is pending or the RequestId
    /// does not match, matching the exception type used by both callers prior to this extraction.
    /// </summary>
    public void ValidateSubmitApproval(DurableApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (_pendingApproval is null)
        {
            throw new InvalidOperationException(
                "No approval request is pending. Ensure RequestApprovalAsync was called first.");
        }

        if (_pendingApproval.RequestId != decision.RequestId)
        {
            throw new InvalidOperationException(
                $"Decision RequestId '{decision.RequestId}' does not match pending request '{_pendingApproval.RequestId}'.");
        }
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the pending request, then blocks the workflow via
    /// <see cref="Workflow.WaitConditionAsync"/> until a matching decision arrives or
    /// <paramref name="approvalTimeout"/> elapses.
    /// </summary>
    /// <param name="request">The approval request that was validated by <see cref="ValidateRequestApproval"/>.</param>
    /// <param name="approvalTimeout">How long to wait before auto-denying.</param>
    /// <param name="onRequested">
    /// Optional callback fired once when the request is stored.
    /// Receives the request so callers can emit structured log events.
    /// </param>
    /// <param name="onResolved">
    /// Optional callback fired once when the request resolves (approved, rejected, or timed out).
    /// Receives the final decision so callers can emit structured log events.
    /// </param>
    public async Task<DurableApprovalDecision> RequestApprovalAsync(
        DurableApprovalRequest request,
        TimeSpan approvalTimeout,
        Action<DurableApprovalRequest>? onRequested = null,
        Action<DurableApprovalDecision>? onResolved = null)
    {
        // Guard against concurrent requests. ValidateRequestApproval runs this check on the
        // [WorkflowUpdateValidator] path (update arrives over the wire), but direct turn-loop
        // calls bypass that callback entirely. Running the check here means both paths are
        // protected; the double-check on the update path is harmless.
        if (_pendingApproval is not null)
            throw new InvalidOperationException(
                "An approval request is already pending. Submit or timeout the current request before sending another.");

        // The caller supplies the request content; the workflow is the authority for its
        // deadline. Copy rather than mutate so a request object reused by a caller cannot
        // introduce a stale or inconsistent expiration timestamp.
        request = new DurableApprovalRequest
        {
            RequestId = request.RequestId,
            FunctionName = request.FunctionName,
            CallId = request.CallId,
            Description = request.Description,
            ReviewData = request.ReviewData,
            ExpiresAt = Workflow.UtcNow + approvalTimeout,
        };

        _pendingApproval = request;
        _approvalDecision = null;

        onRequested?.Invoke(request);

        var conditionMet = await Workflow.WaitConditionAsync(
            () => _approvalDecision is not null && _approvalDecision.RequestId == request.RequestId,
            timeout: approvalTimeout);

        if (!conditionMet)
        {
            var timedOutDecision = new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = false,
                Reason = $"Approval timed out after {approvalTimeout.TotalHours:F0} hours with no human response.",
            };

            onResolved?.Invoke(timedOutDecision);   // callback first
            RememberResolution(timedOutDecision);
            _pendingApproval = null;                // then clear state
            _approvalDecision = null;
            return timedOutDecision;
        }

        var decision = _approvalDecision!;

        onResolved?.Invoke(decision);   // callback first
        RememberResolution(decision);
        _pendingApproval = null;        // then clear state
        _approvalDecision = null;
        return decision;
    }

    /// <summary>
    /// Stores the human decision, unblocking the <see cref="RequestApprovalAsync"/> wait condition.
    /// </summary>
    public void SubmitApproval(DurableApprovalDecision decision)
    {
        _approvalDecision = decision;
    }

    /// <summary>
    /// Resolves the current approval request with retry-safe result semantics.
    /// </summary>
    public DurableApprovalResolutionResult ResolveApproval(DurableApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var retained = _resolvedApprovals.LastOrDefault(
            existing => string.Equals(existing.RequestId, decision.RequestId, StringComparison.Ordinal));

        if (_pendingApproval is null)
        {
            if (retained is null)
            {
                return Result(DurableApprovalResolutionStatus.NotPending, decision.RequestId);
            }

            return Result(
                IsEquivalent(retained, decision)
                    ? DurableApprovalResolutionStatus.AlreadyResolved
                    : DurableApprovalResolutionStatus.Conflict,
                decision.RequestId);
        }

        if (!string.Equals(_pendingApproval.RequestId, decision.RequestId, StringComparison.Ordinal))
        {
            if (retained is not null)
            {
                return Result(
                    IsEquivalent(retained, decision)
                        ? DurableApprovalResolutionStatus.AlreadyResolved
                        : DurableApprovalResolutionStatus.Conflict,
                    decision.RequestId);
            }

            return Result(DurableApprovalResolutionStatus.RequestMismatch, decision.RequestId);
        }

        if (_approvalDecision is not null)
        {
            return Result(
                IsEquivalent(_approvalDecision, decision)
                    ? DurableApprovalResolutionStatus.AlreadyResolved
                    : DurableApprovalResolutionStatus.Conflict,
                decision.RequestId);
        }

        _approvalDecision = decision;
        return Result(DurableApprovalResolutionStatus.Accepted, decision.RequestId);
    }

    /// <summary>
    /// Returns the currently pending approval request, or <see langword="null"/> if none.
    /// </summary>
    public DurableApprovalRequest? GetPendingApproval() => _pendingApproval;

    private void RememberResolution(DurableApprovalDecision decision)
    {
        _resolvedApprovals.RemoveAll(existing =>
            string.Equals(existing.RequestId, decision.RequestId, StringComparison.Ordinal));
        _resolvedApprovals.Add(decision);
        if (_resolvedApprovals.Count > MaxRetainedResolutions)
        {
            _resolvedApprovals.RemoveRange(0, _resolvedApprovals.Count - MaxRetainedResolutions);
        }
    }

    private static bool IsEquivalent(DurableApprovalDecision left, DurableApprovalDecision right) =>
        left.Approved == right.Approved &&
        string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);

    private static DurableApprovalResolutionResult Result(
        DurableApprovalResolutionStatus status,
        string requestId) => new()
        {
            RequestId = requestId,
            Status = status,
        };
}
