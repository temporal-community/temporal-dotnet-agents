using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Result of a single durable model step. Returned by the
/// <c>TemporalCommunity.Extensions.AI.GetChatStep</c> activity and consumed by
/// <see cref="DurableChatWorkflow"/> to drive the durable tool-dispatch loop.
/// </summary>
internal sealed class DurableChatStepResult
{
    /// <summary>
    /// True when this step terminates the managed loop, either with a final response or an
    /// incomplete response. False only when the workflow must dispatch the contained
    /// <see cref="ToolCalls"/> and call back into the LLM.
    /// </summary>
    public required bool IsFinal { get; init; }

    /// <summary>
    /// The messages produced by the LLM for this step, preserving their roles, order, and
    /// MEAI content instead of reconstructing a single assistant message.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// The unresolved, non-informational tool-call requests extracted from assistant messages
    /// in <see cref="Messages"/>, in the order they appeared. Null when the step is
    /// terminal, including when provider output contained calls that the completion policy did
    /// not authorize for dispatch.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FunctionCallContent>? ToolCalls { get; init; }

    /// <summary>
    /// Token-usage details reported by the model for this step, when available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDetails? Usage { get; init; }

    /// <summary>The provider-reported reason that this model step stopped, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>The provider response ID returned for this model step, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; init; }

    /// <summary>The model ID reported for this model step, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; init; }

    /// <summary>Serializable additional metadata returned for this model step, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

    /// <summary>
    /// Gets the terminal completion reason when this step must end the managed loop. The default
    /// preserves legacy activity payloads that predate this field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DurableTurnCompletionReason CompletionReason { get; init; } =
        DurableTurnCompletionReason.FinalResponse;
}
