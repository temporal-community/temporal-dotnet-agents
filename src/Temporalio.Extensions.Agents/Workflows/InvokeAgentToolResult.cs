using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Result payload returned by the per-tool activity
/// (<c>Temporalio.Extensions.Agents.InvokeAgentTool</c>). Carries the tool's return value
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
