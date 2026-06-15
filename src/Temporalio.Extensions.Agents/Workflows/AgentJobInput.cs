using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.Agents.Scheduling;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentJobWorkflow"/> for a single, isolated agent run.
/// Unlike <see cref="AgentWorkflowInput"/>, there is no conversation history, StateBag,
/// TTL, or continue-as-new — the job runs once and completes.
/// </summary>
internal sealed record AgentJobInput
{
    /// <summary>Gets the name of the agent to invoke.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>Gets the run request (messages + options) for this job.</summary>
    public required RunRequest Request { get; init; }

    /// <summary>
    /// Gets the activity timeout for the agent activity invocation.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the heartbeat timeout for the agent activity invocation.
    /// Defaults to 2 minutes.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets the retry policy applied to the agent activity invocation.
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// When a tool name is present, <see cref="AgentJobWorkflow"/> uses these options for the
    /// per-tool activity dispatch; otherwise it falls back to a default built from
    /// <see cref="ActivityTimeout"/> and <see cref="RetryPolicy"/>.
    /// Write tools registered with <c>opts.NoRetry()</c> require this to be populated so that
    /// <c>MaximumAttempts = 1</c> is respected in scheduled / deferred jobs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ActivityOptions>? DurableAgentToolActivityOptions { get; init; }

    /// <summary>
    /// Maximum number of tool-call iterations per turn. Mirrors
    /// <see cref="DurableAgentBuilder.MaxToolCallsPerTurn"/>. Defaults to 20.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 20;

    // Feature L — interceptor plumbing for AgentJobWorkflow (resolved at runtime from
    // CachedDurableAgent; not frozen in workflow input like AgentWorkflow's
    // ProxyResolvedWorkerConfig approach, since AgentJobWorkflow is fire-and-forget
    // without a resolution handshake).

    /// <summary>
    /// Pre-computed <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches.
    /// <see langword="null"/> when no interceptor is configured.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivityOptions? InterceptorActivityOptions { get; init; }

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches where the
    /// tool has an explicit <see cref="DurableToolOptions.InterceptorTimeout"/> set.
    /// Falls back to <see cref="InterceptorActivityOptions"/> for tools not in this map.
    /// <see langword="null"/> when no tool carries a custom interceptor timeout.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ActivityOptions>? InterceptorToolActivityOptions { get; init; }

    /// <summary>
    /// Names of tools that skip the interceptor (have <c>SkipInterceptor()</c> set).
    /// <see langword="null"/> is equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InterceptorSkippedTools { get; init; }

    /// <summary>
    /// Names of tools that require approval even when the interceptor returns Proceed.
    /// Only non-scope-aware required tools appear here (Rule 2 — absolute approval floor).
    /// <see langword="null"/> is equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiresApprovalTools { get; init; }

    // Feature B — scope-aware tool lists for AgentJobWorkflow interceptor dispatch.

    /// <summary>
    /// Names of tools registered with <c>ScopeAware()</c>. The workflow passes
    /// <c>ScopeAware = true</c> on the interceptor input for these tools.
    /// <see langword="null"/> is equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ScopeAwareTools { get; init; }

    /// <summary>
    /// Names of tools registered with both <c>ScopeAware()</c> and <c>RequireApproval()</c>.
    /// These tools are NOT in <see cref="RequiresApprovalTools"/> — the interceptor is responsible
    /// for enforcing the approval gate. Only meaningful for <see cref="AgentJobWorkflow"/> as a
    /// diagnostic: since job workflows have no HITL loop, <c>PauseForApproval</c> from the
    /// interceptor degrades to <c>Block</c>.
    /// <see langword="null"/> is equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ScopeAwareApprovalTools { get; init; }
}
