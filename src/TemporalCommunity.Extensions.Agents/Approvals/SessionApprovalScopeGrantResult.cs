using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>Result of a privileged session-scope grant request.</summary>
public sealed class SessionApprovalScopeGrantResult
{
    /// <summary>The approval resolution outcome for this privileged scope-grant request.</summary>
    public required DurableApprovalResolutionResult Resolution { get; init; }

    /// <summary>
    /// Stable grant identifier when the request was accepted or had already been accepted.
    /// Null when the request was rejected as not pending or conflicting.
    /// </summary>
    public string? GrantId { get; init; }
}
