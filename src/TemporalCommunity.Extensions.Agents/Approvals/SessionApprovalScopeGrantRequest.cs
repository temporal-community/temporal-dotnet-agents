namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// Privileged request to approve one pending call and create a reusable grant for the current
/// agent session.
/// </summary>
/// <remarks>
/// <para>
/// This data is not proof of reviewer identity or authorization. The administrative service must
/// be called only after the application authenticates the actor and authorizes access to the
/// selected <c>TemporalAgentSessionId</c>. Actor and reason are untrusted audit strings.
/// </para>
/// <para>
/// Exactly one of <see cref="Pattern"/> or <see cref="MatchAllArguments"/> must be supplied.
/// </para>
/// </remarks>
public sealed class SessionApprovalScopeGrantRequest
{
    /// <summary>The pending approval request to approve.</summary>
    public required string RequestId { get; init; }

    /// <summary>An explicit argument constraint for future matching calls.</summary>
    public ApprovalScopePattern? Pattern { get; init; }

    /// <summary>Explicitly grants every argument combination for the pending tool.</summary>
    public bool MatchAllArguments { get; init; }

    /// <summary>Workflow-time instant after which the grant cannot auto-approve a call.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Optional, untrusted actor label retained for audit.</summary>
    public string? Actor { get; init; }

    /// <summary>Optional, untrusted reason retained for audit.</summary>
    public string? Reason { get; init; }
}
