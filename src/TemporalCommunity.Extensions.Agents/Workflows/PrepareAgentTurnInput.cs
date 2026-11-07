using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.State;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed class PrepareAgentTurnInput
{
    public required string AgentName { get; init; }

    public required string SessionId { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? MemoryScope { get; init; }

    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}

internal sealed class PreparedAgentTurn
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PreparedMessageAttribution>? MessageAttributions { get; init; }

    public string? Instructions { get; init; }

    public required IReadOnlyList<PreparedAgentTool> Tools { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasToolMiddleware { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StateBagDelta? StateBagDelta { get; init; }
}
