namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Describes an accepted resolution of a durable approval request.
/// </summary>
public sealed class DurableApprovalResolutionResult
{
    /// <summary>Gets the request ID addressed by the resolution attempt.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the resolution outcome.</summary>
    public required DurableApprovalResolutionStatus Status { get; init; }
}

/// <summary>
/// Resolution classifications. Supported workflow-update APIs accept only a matching pending
/// decision; non-progressing classifications are rejected by the update validator before
/// acceptance rather than returned as successful update results.
/// </summary>
public enum DurableApprovalResolutionStatus
{
    /// <summary>The decision was accepted for the currently pending request.</summary>
    Accepted,

    /// <summary>The request was already resolved; a repeated workflow update is rejected.</summary>
    AlreadyResolved,

    /// <summary>No pending or retained approval request has the supplied request ID.</summary>
    NotPending,

    /// <summary>A different approval request is currently pending.</summary>
    RequestMismatch,

    /// <summary>The supplied decision conflicts with the already accepted decision.</summary>
    Conflict,
}
