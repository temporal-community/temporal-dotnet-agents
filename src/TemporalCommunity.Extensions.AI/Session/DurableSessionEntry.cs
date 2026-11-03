using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Session;

/// <summary>
/// Wire shape for durable chat session history entries. Concrete subclasses are
/// <see cref="DurableSessionRequest"/> and <see cref="DurableSessionResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>$type</c> discriminator strings (<c>"ai_request"</c>, <c>"ai_response"</c>)
/// are wire-format constants embedded in workflow event history forever — do not change.
/// </para>
/// <para>
/// Library-created entries receive a stable <see cref="EntryId"/> and share a
/// <see cref="TurnId"/> with the paired request or response. Correlation IDs remain an
/// observability field and are not unique entry identifiers.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(DurableSessionRequest), "ai_request")]
[JsonDerivedType(typeof(DurableSessionResponse), "ai_response")]
[JsonDerivedType(typeof(CompactionMarkerEntry), "compaction-marker")]
[JsonDerivedType(typeof(TruncationMarkerEntry), "truncation-marker")]
public abstract class DurableSessionEntry
{
    private IReadOnlyList<ChatMessage> messages = [];

    /// <summary>Stable library identity for this retained history entry.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; internal set; }

    /// <summary>Stable identity shared by entries belonging to one logical turn.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; internal set; }

    /// <summary>
    /// Per-turn correlation identifier used for log/trace threading and for matching
    /// request entries to response entries within a session.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Creation timestamp of this entry. Workflow callers should source this from
    /// <c>Workflow.UtcNow</c>; activity / external callers should use
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The chat messages associated with this entry. For request entries this is
    /// typically the user-supplied prompt(s); for response entries it is the model's
    /// reply (which may include tool-call / tool-result content turns).
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get => this.messages;
        init => this.messages = value ?? [];
    }

    /// <summary>
    /// Captures additional JSON properties that round-trip through serialization but
    /// have no first-class field on the entry shape. Useful for forward compatibility.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
