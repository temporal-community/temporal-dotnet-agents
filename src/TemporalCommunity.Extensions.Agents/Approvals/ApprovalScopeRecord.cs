namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// A bounded, expiring approval grant stored as a JSON array in the session
/// <see cref="Microsoft.Agents.AI.AgentSessionStateBag"/>.
/// </summary>
public sealed class ApprovalScopeRecord
{
    /// <summary>Stable identifier used for explicit revocation.</summary>
    public string GrantId { get; init; } = string.Empty;

    /// <summary>The tool name this scope covers.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Optional argument pattern. <see langword="null"/> = match any call of this tool.
    /// </summary>
    public ApprovalScopePattern? Pattern { get; init; }

    /// <summary>Whether this grant explicitly matches every argument combination.</summary>
    public bool MatchAllArguments { get; init; }

    /// <summary>When the scope was granted. Used for audit and expiry calculations.</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>Workflow-time expiry after which the grant does not match.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The RequestId of the approval that produced this scope.</summary>
    public required string OriginatingRequestId { get; init; }

    /// <summary>Optional, untrusted actor label retained for audit.</summary>
    public string? Actor { get; init; }

    /// <summary>Optional, untrusted reason retained for audit.</summary>
    public string? Reason { get; init; }
}
