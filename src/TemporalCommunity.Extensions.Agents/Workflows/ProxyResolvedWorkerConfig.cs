using System.Text.Json.Serialization;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Bundle of worker-side settings that are either baked into <see cref="AgentWorkflowInput"/>
/// up front (when the workflow is started by a process that hosts the full
/// <c>DurableAgentRegistration</c>) or resolved lazily on the first step of the first turn
/// (when the workflow is started by a proxy-only client that does not know the worker-side
/// registration).
/// </summary>
/// <remarks>
/// <para>
/// Consolidates the flat fields that previously lived directly on
/// <see cref="AgentWorkflowInput"/> (and on <see cref="AgentStepResult"/> as the "Resolved*"
/// trio) so that:
/// </para>
/// <list type="bullet">
/// <item>
/// On <see cref="AgentWorkflowInput.ResolvedWorkerConfig"/>, a non-null value means the
/// workflow already has worker-side settings to use (no resolution handshake needed).
/// A null value signals the workflow is proxy-started and must ask the worker via
/// <see cref="AgentStepInput.NeedsWorkerSettingsResolution"/>.
/// </item>
/// <item>
/// On <see cref="AgentStepResult.ResolvedWorkerConfig"/>, a non-null value is the worker's
/// response to a resolution request. The workflow patches this onto its <c>_input</c> for
/// subsequent iterations (and carries it forward across continue-as-new).
/// </item>
/// </list>
/// </remarks>
internal sealed record ProxyResolvedWorkerConfig
{
    /// <summary>
    /// Maximum number of LLM-step iterations within a single agent turn (resolved from the
    /// worker's <c>DurableAgentRegistration.MaxToolCallsPerTurn</c>, default 20).
    /// </summary>
    public required int MaxToolCallsPerTurn { get; init; }

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name
    /// (case-insensitive). When a tool name is present, the workflow uses these options for the
    /// per-tool activity dispatch; otherwise it falls back to a default built from the
    /// workflow-level activity-timeout/retry-policy fields.
    /// </summary>
    public required IReadOnlyDictionary<string, ActivityOptions> ToolActivityOptions { get; init; }

    /// <summary>
    /// Pre-computed <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches that
    /// do not have a per-tool override. Used as the fallback when a tool has no entry in
    /// <see cref="InterceptorToolActivityOptions"/>.
    /// Nullable and NOT <c>required</c> for forward-compat — <see langword="null"/> means no
    /// interceptor is configured, or the config predates Feature L.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivityOptions? InterceptorActivityOptions { get; init; }

    /// <summary>
    /// Per-tool <see cref="ActivityOptions"/> for <c>RunToolInterceptor</c> dispatches where the
    /// tool has an explicit <see cref="DurableToolOptions.InterceptorTimeout"/> set.
    /// The workflow uses this entry when present; falls back to <see cref="InterceptorActivityOptions"/>
    /// otherwise. Only populated when at least one tool carries a custom interceptor timeout.
    /// Nullable and NOT <c>required</c> for forward-compat.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ActivityOptions>? InterceptorToolActivityOptions { get; init; }

    /// <summary>
    /// Names of tools that have <see cref="DurableToolOptions.SkipInterceptorFlag"/> set.
    /// The workflow skips <c>RunToolInterceptor</c> for these tools even when an interceptor
    /// is configured.
    /// Nullable and NOT <c>required</c> for forward-compat — <see langword="null"/> is
    /// equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InterceptorSkippedTools { get; init; }

    /// <summary>
    /// Names of every tool that has <see cref="DurableToolOptions.RequireApprovalFlag"/> set.
    /// The workflow forces a workflow-parked approval for these tools even if the interceptor
    /// returns <c>Proceed</c> (Rule 2 — absolute configuration-time floor).
    /// Nullable and NOT <c>required</c> for forward-compat — <see langword="null"/> is
    /// equivalent to an empty list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiresApprovalTools { get; init; }

}
