namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Serializable approval request stored in workflow state.
/// Represents a pending tool approval that blocks the workflow until a human responds.
/// </summary>
public sealed class DurableApprovalRequest
{
    /// <summary>
    /// The unique identifier correlating this request with its response.
    /// Maps to <c>ToolApprovalRequestContent.RequestId</c>.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The name of the function that requires approval.
    /// </summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// The tool call ID from the LLM's function call request.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Human-readable description of what the tool call will do.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Caller-authored, reviewer-safe context for the approval decision.
    /// </summary>
    /// <remarks>
    /// The durable tool loop never copies raw model-supplied function arguments into this
    /// property. Populate it only from data intentionally selected for the reviewer, such as
    /// an account identifier, operation summary, or policy reference. This data is not a
    /// trusted actor identity, credential, or authorization claim. The approval UI must
    /// authenticate its reviewer independently, and the tool must authorize the effect against
    /// current authoritative state immediately before executing it.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ReviewData { get; init; }

    /// <summary>
    /// Workflow-time deadline after which the request is automatically rejected.
    /// Set by the workflow when the request becomes pending.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
