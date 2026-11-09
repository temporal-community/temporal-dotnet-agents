using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <c>RunDurableAgentStepAsync</c> activity used by durable-agent workflows.
/// Each call produces either a final assistant message or a list of pending
/// <see cref="FunctionCallContent"/> items that the workflow then dispatches as separate
/// <c>InvokeAgentToolAsync</c> activities (one per tool call, serially in model order).
/// </summary>
internal sealed class AgentStepInput
{
    /// <summary>Gets the name of the agent registered with <see cref="TemporalAgentsOptions"/>.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the originating run request (carries response format, correlation id, etc.).</summary>
    public required RunRequest Request { get; init; }

    /// <summary>
    /// Provider context prepared once at the beginning of a logical agent turn. When supplied,
    /// this step must not invoke provider hooks again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreparedInstructions { get; init; }

    public bool ProvidersPrepared { get; init; }

    /// <summary>
    /// Frozen registered tools selected by provider preparation for this logical turn.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PreparedAgentTool>? PreparedTools { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PreparedMessageAttribution>? AccumulatedMessageAttributions { get; init; }

    /// <summary>
    /// Gets the messages accumulated across the step loop. On the first iteration this contains
    /// the original user request messages; on subsequent iterations it also contains the
    /// assistant tool-call message and the prior <see cref="FunctionResultContent"/> messages.
    /// </summary>
    public required List<ChatMessage> AccumulatedMessages { get; init; }

    /// <summary>Start of the active turn in the flattened model-message list.</summary>
    public int CurrentTurnStartIndex { get; init; }

    /// <summary>Prior complete turns eligible for budget-driven trimming.</summary>
    public IReadOnlyList<DurableChatTurnRange>? PreviousTurnRanges { get; init; }

    /// <summary>Maximum estimated model-context size, including the output reserve.</summary>
    public int? MaxModelContextTokens { get; init; }

    /// <summary>Token reserve for model output.</summary>
    public int ModelOutputTokenReserve { get; init; } = 1024;

    /// <summary>
    /// Gets the serialized <see cref="State.AgentSessionStateBag"/> from the previous step
    /// (or turn). Threaded through every iteration of the loop.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? MemoryScope { get; init; }

    /// <summary>
    /// Gets the explicit session ID for this agent call. When provided, the activity uses this
    /// instead of parsing the workflow ID from the activity context.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalAgentSessionId? SessionId { get; init; }

    /// <summary>Maximum serialized activity payload size, including codec expansion allowance.</summary>
    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Allowance reserved for payload codec expansion.</summary>
    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}
