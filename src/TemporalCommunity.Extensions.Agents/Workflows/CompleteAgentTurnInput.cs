using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.State;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed class CompleteAgentTurnInput
{
    public required string AgentName { get; init; }

    public required string SessionId { get; init; }

    public required IReadOnlyList<ChatMessage> RequestMessages { get; init; }

    public required IReadOnlyList<ChatMessage> ResponseMessages { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? MemoryScope { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvokeExceptionMessage { get; init; }

    public bool IsCancellation { get; init; }

    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}

internal sealed class CompletedAgentTurn
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StateBagDelta? StateBagDelta { get; init; }
}
