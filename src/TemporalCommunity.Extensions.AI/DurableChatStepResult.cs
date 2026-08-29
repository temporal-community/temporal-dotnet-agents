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
    /// The assistant message produced by the LLM for this step. Always present —
    /// even when the message is empty of text and only contains
    /// <see cref="FunctionCallContent"/> entries.
    /// </summary>
    public required ChatMessage AssistantMessage { get; init; }

    /// <summary>
    /// The tool-call requests extracted from <see cref="AssistantMessage"/>, in the
    /// order they appeared. Null when the step is terminal, including when provider output
    /// contained calls that the completion policy did not authorize for dispatch.
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

    /// <summary>
    /// Gets the terminal completion reason when this step must end the managed loop. The default
    /// preserves legacy activity payloads that predate this field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DurableTurnCompletionReason CompletionReason { get; init; } =
        DurableTurnCompletionReason.FinalResponse;
}
