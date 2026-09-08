using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// An <see cref="AgentSession"/> implementation for Temporal agents.
/// </summary>
public sealed class TemporalAgentSession : AgentSession
{
    /// <summary>
    /// Initializes a new <see cref="TemporalAgentSession"/> with the given session ID.
    /// Use this to reconnect to an existing agent session by its known workflow ID.
    /// </summary>
    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
    }

    [JsonConstructor]
    internal TemporalAgentSession(
        TemporalAgentSessionId sessionId,
        AgentSessionStateBag stateBag,
        IReadOnlyList<DurableSessionEntry>? history = null,
        SessionHistoryMetadata? historyMetadata = null) : base(stateBag)
    {
        this.SessionId = sessionId;
        this.History = history ?? [];
        this.HistoryMetadata = historyMetadata;
    }

    /// <summary>Gets the Temporal agent session ID.</summary>
    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    /// <summary>
    /// Gets the session's conversation history (request/response entries and markers).
    /// Internal storage; mutated by TemporalAIAgent and restored via deserialization.
    /// </summary>
    internal IReadOnlyList<DurableSessionEntry> History { get; private set; } = [];

    /// <summary>
    /// Gets metadata about session history (compaction, entry counts).
    /// Internal storage; carried forward across continue-as-new.
    /// </summary>
    internal SessionHistoryMetadata? HistoryMetadata { get; set; }

    /// <summary>
    /// Gets a read-only snapshot of the session's conversation history.
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> GetHistory() => this.History;

    /// <summary>
    /// Gets the total number of history entries in this session.
    /// </summary>
    public int HistoryEntryCount => this.History.Count;

    /// <summary>
    /// JSON property for serializing history. Excludes nulls/empty lists to save space.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyList<DurableSessionEntry>? SerializedHistory
    {
        get => this.History.Count > 0 ? this.History : null;
        init => this.History = value ?? [];
    }

    /// <summary>
    /// JSON property for serializing history metadata.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("historyMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal SessionHistoryMetadata? SerializedHistoryMetadata
    {
        get => this.HistoryMetadata;
        init => this.HistoryMetadata = value;
    }

    /// <summary>
    /// Appends an entry to the session's history. Called by TemporalAIAgent.RunCore
    /// after each agent turn.
    /// </summary>
    internal void AppendHistoryEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var mutableHistory = new List<DurableSessionEntry>(this.History) { entry };
        this.History = mutableHistory.AsReadOnly();
    }

    /// <summary>
    /// Replaces the session's history (for compaction or migration scenarios).
    /// </summary>
    internal void SetHistory(IReadOnlyList<DurableSessionEntry> newHistory)
    {
        ArgumentNullException.ThrowIfNull(newHistory);
        this.History = newHistory;
    }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(typeof(TemporalAgentSession)));
    }

    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (!serializedSession.TryGetProperty("sessionId", out JsonElement sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Invalid or missing sessionId property.");
        }

        string sessionIdString = sessionIdElement.GetString() ?? throw new JsonException("sessionId property is null.");
        TemporalAgentSessionId sessionId = TemporalAgentSessionId.Parse(sessionIdString);
        AgentSessionStateBag stateBag = serializedSession.TryGetProperty("stateBag", out JsonElement stateBagElement)
            ? AgentSessionStateBag.Deserialize(stateBagElement)
            : new AgentSessionStateBag();

        // Restore history entries from serialized form.
        IReadOnlyList<DurableSessionEntry>? history = null;
        if (serializedSession.TryGetProperty("history", out JsonElement historyElement) &&
            historyElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            history = JsonSerializer.Deserialize<IReadOnlyList<DurableSessionEntry>>(
                historyElement,
                jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions)
                ?? [];
        }

        // Restore history metadata.
        SessionHistoryMetadata? historyMetadata = null;
        if (serializedSession.TryGetProperty("historyMetadata", out JsonElement metadataElement) &&
            metadataElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            historyMetadata = JsonSerializer.Deserialize<SessionHistoryMetadata>(
                metadataElement,
                jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions);
        }

        return new TemporalAgentSession(sessionId, stateBag, history, historyMetadata);
    }

    /// <summary>
    /// Creates a <see cref="TemporalAgentSession"/> with the given <paramref name="sessionId"/>
    /// and optionally restores a <see cref="AgentSessionStateBag"/> from a previously
    /// serialized value (see <see cref="SerializeStateBag"/>).
    /// </summary>
    internal static TemporalAgentSession FromStateBag(
        TemporalAgentSessionId sessionId,
        JsonElement? serializedStateBag)
    {
        // Note: a JsonElement default-initialized in C# has ValueKind == Undefined,
        // whereas an explicit JSON null has ValueKind == Null. After round-tripping
        // through Temporal's payload converter, an absent bag may surface as either
        // a C# null on the JsonElement? wrapper or as a JsonElement whose ValueKind
        // is Undefined or Null. The pattern below covers all cases by treating
        // anything other than a real value as "no bag" and returning a fresh session.
        if (serializedStateBag is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bagEl)
        {
            return new TemporalAgentSession(sessionId, AgentSessionStateBag.Deserialize(bagEl), history: null);
        }

        return new TemporalAgentSession(sessionId);
    }

    /// <summary>
    /// Serializes the <see cref="AgentSessionStateBag"/> portion of this session,
    /// returning <see langword="null"/> when the bag is empty.
    /// </summary>
    internal JsonElement? SerializeStateBag()
    {
        if (this.StateBag.Count == 0)
        {
            return null;
        }

        return this.StateBag.Serialize();
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
        {
            return this.SessionId;
        }

        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public override string ToString() => this.SessionId.WorkflowId;
}
