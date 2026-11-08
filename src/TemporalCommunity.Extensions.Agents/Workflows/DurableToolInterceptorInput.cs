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
    /// <see cref="DurableToolOptions.RequireApproval()"/>.
    /// </summary>
    /// <remarks>
    /// JSON property name must remain stable for replay.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresApproval { get; init; }

}
