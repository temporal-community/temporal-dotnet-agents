using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Result of a single Pattern 3 LLM step. Returned by the
/// <c>TemporalCommunity.Extensions.AI.GetChatStep</c> activity and consumed by
/// <see cref="DurableChatWorkflow"/> to drive the durable tool-dispatch loop.
/// </summary>
internal sealed class DurableChatStepResult
{
    /// <summary>
    /// True when the LLM produced no tool-call requests and the assistant message
    /// represents the final response for this turn. False when the workflow must
    /// dispatch the contained <see cref="ToolCalls"/> and call back into the LLM.
    /// </summary>
    public required bool IsFinal { get; init; }

    /// <summary>
    /// The assistant message produced by the LLM for this step. Always present —
    /// even when the message is empty of text and only contains
    /// <see cref="FunctionCallContent"/> entries.
    /// </summary>
    public required ChatMessage AssistantMessage { get; init; }

    /// <summary>
    /// The tool-call requests extracted from <see cref="AssistantMessage"/>, in the
    /// order they appeared. Null or empty when <see cref="IsFinal"/> is <c>true</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FunctionCallContent>? ToolCalls { get; init; }

    /// <summary>
    /// Token-usage details reported by the model for this step, when available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDetails? Usage { get; init; }
}
