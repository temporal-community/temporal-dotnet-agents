using System.Text.Json;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <c>RunToolInterceptor</c> activity.
/// Carries enough context for the interceptor to make a pre-tool decision.
/// </summary>
/// <remarks>
/// JSON property names on this workflow payload must remain stable for replay.
/// </remarks>
internal sealed class DurableToolInterceptorInput
{
    /// <summary>Name of the agent that owns this tool call.</summary>
    public required string AgentName { get; init; }

    /// <summary>Name of the tool being intercepted.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Arguments the LLM supplied for this tool call. May be <see langword="null"/> when the
    /// LLM did not emit any arguments (parameterless tool calls).
    /// </summary>
    public Dictionary<string, object?>? Arguments { get; init; }

    /// <summary>LLM-assigned call ID. May be <see langword="null"/>.</summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Serialized <c>AgentSessionStateBag</c> snapshot. Deserialized inside the activity to
    /// an <c>AgentSessionStateBag?</c> before constructing <see cref="AgentToolContext"/>.
    /// Uses the same <c>FromStateBag</c> pattern as <c>RunDurableAgentStep</c>.
    /// </summary>
    public JsonElement? SerializedStateBag { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the tool was registered with
    /// <see cref="DurableToolOptions.ScopeAware()"/> and the interceptor should consult
    /// session and always-scope records before deciding.
    /// </summary>
    /// <remarks>JSON property name must remain stable for replay.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ScopeAware { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the tool was registered with
    /// <see cref="DurableToolOptions.RequireApproval()"/>. This is true for both scope-aware
    /// and non-scope-aware required tools.
    /// </summary>
    /// <remarks>
    /// This is intentionally broader than <c>ProxyResolvedWorkerConfig.RequiresApprovalTools</c>,
    /// which excludes scope-aware tools. The interceptor needs this flag so it can return
    /// <c>PauseForApproval</c> when no matching scope record is found.
    /// JSON property name must remain stable for replay.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresApproval { get; init; }

    /// <summary>Replay-safe workflow time used to evaluate expiring session grants.</summary>
    public DateTimeOffset? ApprovalEvaluationTime { get; init; }
}
