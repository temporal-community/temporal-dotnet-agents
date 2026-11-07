using System.Text.Json;
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.Agents.State;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed class InitializeChatHistoryMemoryInput
{
    public required string AgentName { get; init; }

    public required string SessionId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}

internal sealed class InitializedChatHistoryMemory
{
    public TemporalMemoryScopeSnapshot? Scope { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StateBagDelta? StateBagDelta { get; init; }
}
