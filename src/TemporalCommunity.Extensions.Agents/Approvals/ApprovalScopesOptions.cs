namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// Per-agent configuration for scope-aware approval behavior. Passed to
/// <see cref="DurableAgentBuilder.UseApprovalScopes(System.Action{ApprovalScopesOptions}?)"/>
/// to configure the built-in <c>ScopedApprovalInterceptor</c>.
/// </summary>
public sealed class ApprovalScopesOptions
{
    /// <summary>
    /// Gets or sets the maximum number of reusable grants retained in one session.
    /// Default: 256.
    /// </summary>
    public int MaxSessionScopeRecords { get; set; } = 256;

    /// <summary>
    /// Gets or sets the maximum serialized byte size of reusable session grants.
    /// Default: 32 KiB.
    /// </summary>
    /// <remarks>
    /// This is deliberately below the workflow's StateBag warning threshold so approval grants
    /// cannot consume the full StateBag budget.
    /// </remarks>
    public int MaxSessionScopeBytes { get; set; } = 32 * 1024;
}
