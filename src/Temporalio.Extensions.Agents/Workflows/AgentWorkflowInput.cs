using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// Inherits shared session-loop fields (<see cref="DurableChatWorkflowInput.MaxEntryCount"/>,
/// <see cref="DurableChatWorkflowInput.HistoryReducer"/>, <see cref="DurableChatWorkflowInput.OriginalCreatedAt"/>,
/// <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>, <see cref="DurableChatWorkflowInput.CarriedHistory"/>)
/// from <see cref="DurableChatWorkflowInput"/> per Layer 3 Decision #1.
/// MAF-specific fields (<see cref="AgentName"/>, <see cref="TaskQueue"/>,
/// <see cref="CarriedStateBag"/>, etc.) live on this subclass.
/// </summary>
/// <remarks>
/// Worker-side resolved settings (<see cref="MaxToolCallsPerTurn"/>,
/// <see cref="UseExternalStoreMode"/>, <see cref="DurableAgentToolActivityOptions"/>) are stored
/// in <see cref="ResolvedWorkerConfig"/> as of the maf-gap Step 3c.1 migration. The legacy
/// flat-field names remain as forwarding computed properties so consumers don't need updating;
/// only construction sites assign to <see cref="ResolvedWorkerConfig"/> directly.
/// </remarks>
internal sealed class AgentWorkflowInput : DurableChatWorkflowInput
{
    /// <summary>Gets the name of the agent that this workflow manages.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> carried forward from a
    /// previous run (for continue-as-new scenarios). Allows AIContextProvider state
    /// (e.g. Mem0 thread IDs) to survive workflow continuation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CarriedStateBag { get; init; }

    // RetryPolicy is inherited from DurableChatWorkflowInput (added in S-X-5). It applies to
    // the agent's RunAgentStep activity (the LLM call); per-tool retry policies are configured
    // separately via DurableAgentToolActivityOptions. The MAF construction sites assign it as
    // before — behavior is unchanged, the declaration just moved to the shared base.

    /// <summary>
    /// Gets the bundle of worker-side settings resolved either eagerly at workflow start
    /// (when <c>AddDurableAgent</c> registered this worker) or lazily on the first step of the
    /// first turn (proxy-started workflows). <see langword="null"/> means proxy-started and not
    /// yet resolved — the workflow must request resolution via
    /// <see cref="AgentStepInput.NeedsWorkerSettingsResolution"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyResolvedWorkerConfig? ResolvedWorkerConfig { get; init; }

    // ── Forwarding properties — preserve consumer call sites across the Step 3c.1 migration ──

    /// <summary>
    /// Maximum number of LLM-step iterations within a single agent turn. Each iteration may
    /// dispatch a parallel batch of tool activities. When the cap is exceeded the workflow
    /// returns a structured error response. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.MaxToolCallsPerTurn"/>;
    /// defaults to <c>20</c> when the config has not yet been resolved (proxy-started, pre-handshake).
    /// </summary>
    /// <remarks>
    /// Shadows <see cref="DurableChatWorkflowInput.MaxToolCallsPerTurn"/>. The base property
    /// belongs to MEAI's Pattern 3 dispatch loop and is irrelevant for the MAF agent workflow
    /// (which forwards through <see cref="ResolvedWorkerConfig"/>). The <c>new</c> keyword
    /// preserves the existing MAF forwarding semantics.
    /// </remarks>
    [JsonIgnore]
    public new int MaxToolCallsPerTurn => ResolvedWorkerConfig?.MaxToolCallsPerTurn ?? 20;

    /// <summary>
    /// When <see langword="true"/>, the agent has an <see cref="HistoryStore.IAgentHistoryStore"/>
    /// configured (per-agent or worker-level). Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.UseExternalStoreMode"/>;
    /// defaults to <see langword="false"/> when the config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public bool UseExternalStoreMode => ResolvedWorkerConfig?.UseExternalStoreMode ?? false;

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ToolActivityOptions"/>;
    /// <see langword="null"/> when the config has not yet been resolved.
    /// </summary>
    /// <remarks>
    /// The dictionary is built at workflow start (not at first activity dispatch) so retry
    /// constraints — especially <c>MaximumAttempts = 1</c> on write tools — are pinned at the
    /// time the workflow began running. Continue-as-new carries the same dictionary forward so
    /// retry semantics survive across CAN transitions.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ActivityOptions>? DurableAgentToolActivityOptions =>
        ResolvedWorkerConfig?.ToolActivityOptions;

    /// <summary>
    /// <see langword="true"/> when worker-side settings are already baked into this input (i.e.,
    /// <see cref="ResolvedWorkerConfig"/> is non-<see langword="null"/>). <see langword="false"/>
    /// for proxy-started workflows that must resolve settings via the first-step handshake.
    /// </summary>
    [JsonIgnore]
    public bool WorkerSettingsResolved => ResolvedWorkerConfig is not null;

    // ── Feature L forwarding properties ──

    /// <summary>
    /// Pre-computed <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches.
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.InterceptorActivityOptions"/>;
    /// <see langword="null"/> when no interceptor is configured or config is not yet resolved.
    /// </summary>
    [JsonIgnore]
    public new ActivityOptions? InterceptorActivityOptions =>
        ResolvedWorkerConfig?.InterceptorActivityOptions;

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> for interceptor dispatches with custom timeouts.
    /// Forwards to <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.InterceptorToolActivityOptions"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyDictionary<string, ActivityOptions>? InterceptorToolActivityOptions =>
        ResolvedWorkerConfig?.InterceptorToolActivityOptions;

    /// <summary>
    /// Names of tools that skip the interceptor. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.InterceptorSkippedTools"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyList<string>? InterceptorSkippedTools =>
        ResolvedWorkerConfig?.InterceptorSkippedTools;

    /// <summary>
    /// Names of tools that always require human approval (Rule 2). Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.RequiresApprovalTools"/>.
    /// </summary>
    [JsonIgnore]
    public new IReadOnlyList<string>? RequiresApprovalTools =>
        ResolvedWorkerConfig?.RequiresApprovalTools;

    // ── Feature B — Approval Scopes forwarding properties ──

    /// <summary>
    /// When <see langword="true"/>, approval scopes are enabled for this agent. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.UseApprovalScopes"/>.
    /// Defaults to <see langword="false"/> when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public bool UseApprovalScopes => ResolvedWorkerConfig?.UseApprovalScopes ?? false;

    /// <summary>
    /// When <see langword="true"/>, approval-scope store mode is enabled (always-scope store
    /// configured and approval scopes active). Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.UseApprovalScopeStoreMode"/>.
    /// Defaults to <see langword="false"/> when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public bool UseApprovalScopeStoreMode => ResolvedWorkerConfig?.UseApprovalScopeStoreMode ?? false;

    /// <summary>
    /// The logical store key for always-scope records. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.AlwaysScopesStoreKey"/>.
    /// </summary>
    [JsonIgnore]
    public string? AlwaysScopesStoreKey => ResolvedWorkerConfig?.AlwaysScopesStoreKey;

    /// <summary>
    /// When <see langword="true"/>, always-scopes are loaded from the store at session start.
    /// Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ApplyAlwaysScopesAtSessionStart"/>.
    /// Defaults to <see langword="false"/> when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public bool ApplyAlwaysScopesAtSessionStart => ResolvedWorkerConfig?.ApplyAlwaysScopesAtSessionStart ?? false;

    /// <summary>
    /// Maximum number of always-scope records that may be cached into workflow StateBag.
    /// Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.MaxAlwaysScopeCacheRecords"/>.
    /// Defaults to 0 when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public int MaxAlwaysScopeCacheRecords => ResolvedWorkerConfig?.MaxAlwaysScopeCacheRecords ?? 0;

    /// <summary>
    /// Maximum serialized byte size for the always-scope StateBag cache value.
    /// Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.MaxAlwaysScopeCacheBytes"/>.
    /// Defaults to 0 when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public int MaxAlwaysScopeCacheBytes => ResolvedWorkerConfig?.MaxAlwaysScopeCacheBytes ?? 0;

    /// <summary>
    /// Start-to-close timeout for approval-scope store activities.
    /// Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ApprovalScopeActivityTimeout"/>.
    /// Defaults to <see cref="TimeSpan.Zero"/> when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public TimeSpan ApprovalScopeActivityTimeout => ResolvedWorkerConfig?.ApprovalScopeActivityTimeout ?? TimeSpan.Zero;

    /// <summary>
    /// Maximum attempts for approval-scope store activities.
    /// Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ApprovalScopeActivityMaximumAttempts"/>.
    /// Defaults to 0 when config has not yet been resolved.
    /// </summary>
    [JsonIgnore]
    public int ApprovalScopeActivityMaximumAttempts => ResolvedWorkerConfig?.ApprovalScopeActivityMaximumAttempts ?? 0;

    /// <summary>
    /// Names of tools registered with <c>ScopeAware()</c>. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ScopeAwareTools"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? ScopeAwareTools => ResolvedWorkerConfig?.ScopeAwareTools;

    /// <summary>
    /// Names of tools registered with both <c>ScopeAware()</c> and <c>RequireApproval()</c>.
    /// These tools are NOT in <see cref="RequiresApprovalTools"/>. Forwards to
    /// <see cref="ResolvedWorkerConfig"/>.<see cref="ProxyResolvedWorkerConfig.ScopeAwareApprovalTools"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? ScopeAwareApprovalTools => ResolvedWorkerConfig?.ScopeAwareApprovalTools;
}
