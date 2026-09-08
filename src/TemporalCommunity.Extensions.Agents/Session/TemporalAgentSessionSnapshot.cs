using System.Text.Json;
using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// The wire contract for a serialized <see cref="TemporalAgentSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TemporalAgentSession"/> is deliberately <strong>not</strong> a source-gen
/// serialization root (see CLAUDE.md). Serializing it directly would silently fall back to the
/// reflection resolver and lose the generated-metadata guarantees the rest of the workflow wire
/// format depends on. This DTO is the registered root instead: it is declared in
/// <c>AgentSessionJsonContext</c>, so <c>GetTypeInfo(typeof(TemporalAgentSessionSnapshot))</c>
/// resolves through generated metadata and the polymorphic <see cref="DurableSessionEntry"/>
/// discriminators (<c>agent_request</c> / <c>agent_response</c>) round-trip intact.
/// </para>
/// <para>
/// <strong>Wire compatibility.</strong> <see cref="SessionId"/> keeps the same
/// <c>sessionId</c> property name and lossless <c>ta-{agent}-{key}</c> string shape the previous
/// direct-session format used, and <see cref="StateBag"/> keeps the <c>stateBag</c> name, so
/// snapshots persisted before session-owned history decode unchanged — they simply carry no
/// <see cref="History"/>, which restores as an empty history.
/// </para>
/// <para>
/// Optional members are omitted from the payload rather than written as <c>null</c>. The ignore
/// condition is declared on the members themselves so omission does not depend on the ambient
/// <see cref="JsonSerializerOptions"/> supplied by the caller.
/// </para>
/// </remarks>
internal sealed class TemporalAgentSessionSnapshot
{
    /// <summary>
    /// The complete workflow ID of the session (<c>ta-{agentName}-{key}</c>), as produced by
    /// <see cref="TemporalAgentSessionId.WorkflowId"/> and parsed back by
    /// <see cref="TemporalAgentSessionId.Parse"/>.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>
    /// The serialized <see cref="Microsoft.Agents.AI.AgentSessionStateBag"/>, or
    /// <see langword="null"/> when the bag is empty.
    /// </summary>
    [JsonPropertyName("stateBag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StateBag { get; init; }

    /// <summary>
    /// The session's conversation history in turn order, or <see langword="null"/> when the
    /// session has no history. A snapshot written before session-owned history existed omits this
    /// member; the deserializer treats that as an empty history.
    /// </summary>
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DurableSessionEntry>? History { get; init; }
}
