namespace Temporalio.Extensions.Agents.Approvals;

/// <summary>
/// Per-agent configuration for scope-aware approval behavior. Passed to
/// <see cref="DurableAgentBuilder.UseApprovalScopes(System.Action{ApprovalScopesOptions}?)"/>
/// to configure the built-in <c>ScopedApprovalInterceptor</c>.
/// </summary>
public sealed class ApprovalScopesOptions
{
    /// <summary>
    /// Gets or sets the key under which always-scopes are stored in <see cref="IApprovalScopeStore"/>.
    /// Default: <c>"temporal.approval_scopes.always"</c>.
    /// </summary>
    /// <remarks>
    /// Must be non-null, non-whitespace, and must not equal
    /// <c>"temporal.approval_scopes.session"</c> — the session-scope StateBag key used by
    /// Feature B internally. Setting this to the session-scope key would cause the always-cache
    /// load at session start to overwrite session-scope records written during the previous
    /// continue-as-new run. Startup validation throws <see cref="System.InvalidOperationException"/>
    /// if a collision is detected. The canonical message is:
    /// "ApprovalScopesOptions.AlwaysScopesStoreKey cannot be set to 'temporal.approval_scopes.session' — that key is reserved for session-scope records managed by Feature B internally. Use a different store key."
    /// </remarks>
    public string AlwaysScopesStoreKey { get; set; } = "temporal.approval_scopes.always";

    /// <summary>
    /// Gets or sets the per-agent store factory for always-scopes. When <see langword="null"/>,
    /// the worker-level default <see cref="TemporalAgentsOptions.ApprovalScopeStore"/> is used.
    /// If neither is configured, <see cref="AI.ApprovalScope.Always"/> degrades to
    /// <see cref="AI.ApprovalScope.Session"/> for scope-aware tools.
    /// </summary>
    public Func<IServiceProvider, IApprovalScopeStore>? ApprovalScopeStore { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether always-scopes are loaded from the approval-scope
    /// store at session start and cached in StateBag for the remainder of the session.
    /// Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, always-scopes are NOT loaded or auto-applied at session start.
    /// Decisions with <see cref="AI.ApprovalScope.Always"/> are still persisted when a store is
    /// configured. This option is reserved for applications that want to manage/display always-scopes
    /// through their own UI or policy layer.
    /// </remarks>
    public bool ApplyAlwaysScopesAtSessionStart { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of always-scope records that may be cached into workflow
    /// StateBag at session start or continue-as-new. Default: 256.
    /// </summary>
    public int MaxAlwaysScopeCacheRecords { get; set; } = 256;

    /// <summary>
    /// Gets or sets the maximum serialized byte size for the always-scope StateBag cache value.
    /// Default: 32 KiB.
    /// </summary>
    /// <remarks>
    /// This is deliberately below <c>AgentWorkflow.StateBagSizeWarnThresholdBytes</c> so approval
    /// scopes cannot consume the full StateBag warning budget.
    /// </remarks>
    public int MaxAlwaysScopeCacheBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// Gets or sets the start-to-close timeout for approval-scope store activities.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan ApprovalScopeActivityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum attempts for approval-scope store activities. Default: 3.
    /// </summary>
    public int ApprovalScopeActivityMaximumAttempts { get; set; } = 3;
}
