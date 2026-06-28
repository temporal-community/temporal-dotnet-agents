using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Result payload returned by the per-tool activity
/// (<c>TemporalCommunity.Extensions.Agents.InvokeAgentTool</c>). Carries the tool's return value
/// alongside the originating <c>CallId</c> so the workflow can pair the result with the
/// matching pending tool call.
/// </summary>
internal sealed class InvokeAgentToolResult
{
    /// <summary>
    /// The value returned by the tool's <c>InvokeAsync</c>. Serialized as <c>object?</c> through the
    /// Temporal data converter; consumers typically project this into a
    /// <see cref="Microsoft.Extensions.AI.FunctionResultContent"/> on the workflow side.
    /// </summary>
    /// <remarks>
    /// <strong>Boundary type (S-X-6, accepted limitation).</strong> Because this is declared
    /// <c>object?</c>, the value crosses the activity→workflow boundary as a
    /// <see cref="System.Text.Json.JsonElement"/> after deserialization — the original domain CLR
    /// type is <em>not</em> rehydrated. The workflow embeds this <see cref="System.Text.Json.JsonElement"/>
    /// directly into <see cref="Microsoft.Extensions.AI.FunctionResultContent.Result"/>
    /// (see <c>AgentWorkflow</c> FunctionResultContent construction). Downstream consumers reading
    /// tool results from history therefore observe a <see cref="System.Text.Json.JsonElement"/>, not
    /// the tool's return type. This is intentional: rehydrating domain types would require carrying
    /// type metadata across the boundary and would break replay of histories serialized before such
    /// a change. Consumers that need a typed value should deserialize the
    /// <see cref="System.Text.Json.JsonElement"/> explicitly.
    /// </remarks>
    public object? Result { get; init; }

    /// <summary>
    /// Echo of <see cref="InvokeAgentToolInput.CallId"/> so the workflow can correlate parallel
    /// tool dispatches.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Serialized <c>AgentSessionStateBag</c> reflecting any mutations the tool made to the
    /// session state during invocation (X-1). <see langword="null"/> when the tool did not
    /// change the bag (the common case). The workflow merges this back into its carried
    /// StateBag after the tool fan-out completes, in tool-call index order (later index wins
    /// on key conflict) for replay determinism.
    /// </summary>
    /// <remarks>
    /// Optional and <c>[JsonIgnore(WhenWritingNull)]</c> so in-flight histories serialized
    /// before this field existed continue to replay (wire-compatible).
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedStateBag { get; init; }
}
