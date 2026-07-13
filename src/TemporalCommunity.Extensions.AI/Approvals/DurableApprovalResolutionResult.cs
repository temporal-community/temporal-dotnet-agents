namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Describes the outcome of resolving a durable approval request.
/// </summary>
public sealed class DurableApprovalResolutionResult
{
    /// <summary>Gets the request ID addressed by the resolution attempt.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the resolution outcome.</summary>
    public required DurableApprovalResolutionStatus Status { get; init; }
}

/// <summary>
/// Outcomes returned when a reviewer resolves a durable approval request.
/// </summary>
public enum DurableApprovalResolutionStatus
{
    /// <summary>The decision was accepted for the currently pending request.</summary>
    Accepted,

    /// <summary>The same decision was already accepted and the retry is safe.</summary>
    AlreadyResolved,

    /// <summary>No pending or retained approval request has the supplied request ID.</summary>
    NotPending,

    /// <summary>A different approval request is currently pending.</summary>
    RequestMismatch,

    /// <summary>The supplied decision conflicts with the already accepted decision.</summary>
    Conflict,
}
