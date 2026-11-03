namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// A human's decision on a pending tool approval request.
/// </summary>
/// <remarks>
/// When the pending request has a tool call ID, both <see cref="CallId"/> and
/// <see cref="FunctionName"/> must match the request. Non-tool approval requests can omit them.
/// </remarks>
public sealed class DurableApprovalDecision
{
    /// <summary>
    /// The request ID this decision applies to.
    /// Must match <see cref="DurableApprovalRequest.RequestId"/>.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The tool call ID this decision applies to, when the request represents a tool call.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// The function name this decision applies to, when the request represents a function call.
    /// </summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// Whether the tool call is approved.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Optional reason for approval or rejection.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The deterministic reason an approval wait expired, or <see langword="null"/> when a
    /// reviewer supplied the decision.
    /// </summary>
    public DurableApprovalExpirationReason? ExpirationReason { get; init; }

}
