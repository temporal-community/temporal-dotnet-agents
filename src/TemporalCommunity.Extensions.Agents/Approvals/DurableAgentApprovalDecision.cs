using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// A human decision on a pending agent-tool approval request, including the optional
/// agent-specific reusable approval scope.
/// </summary>
public sealed class DurableAgentApprovalDecision
{
    /// <summary>The request ID this decision applies to.</summary>
    public required string RequestId { get; init; }

    /// <summary>Whether the pending tool invocation is approved.</summary>
    public bool Approved { get; init; }

    /// <summary>Optional explanation recorded with the decision.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Controls how far an approved decision carries forward. The default approves only the
    /// current invocation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApprovalScope Scope { get; init; } = ApprovalScope.ThisCallOnly;

    /// <summary>
    /// Optional argument pattern for a reusable <see cref="Scope"/>. A <see langword="null"/>
    /// pattern applies to every invocation of the approved tool.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApprovalScopePattern? ScopePattern { get; init; }
}
