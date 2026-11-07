using Microsoft.Agents.AI;
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.AI.Internal;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed record PreparedAgentTool
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DurableFunctionDeclarationSnapshot? Declaration { get; init; }

    public bool IsFrozenProviderSelection { get; init; }

    public bool RequiresApproval { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsTemporalMemorySearch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatHistoryMemoryProviderScope? MemorySearchScope { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalMemoryScopeSnapshot? MemoryScope { get; init; }
}
