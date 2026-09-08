using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// An <see cref="AgentSession"/> implementation for Temporal agents.
/// </summary>
/// <remarks>
/// <para>
/// The session — not the agent — owns the conversation history and the
/// <see cref="AgentSession.StateBag"/> for that conversation. One
/// <see cref="TemporalAIAgent"/> instance may drive several concurrent sessions, so any state
/// held on the agent would be shared (and corrupted) across them. Keeping it here is what makes
/// multi-session isolation and continue-as-new carry-forward possible.
/// </para>
/// <para>
/// The wire shape is <see cref="TemporalAgentSessionSnapshot"/>, not this type: see that type's
/// remarks for why direct session serialization is not permitted.
/// </para>
/// </remarks>
public sealed class TemporalAgentSession : AgentSession
{
    // Mutable backing store so AppendHistoryEntry stays O(1). _historyView is created once (field
    // initializers run in declaration order, so _history is already assigned) and handed out
    // instead of _history itself, so callers cannot cast the facade back to a mutable list.
    private readonly List<DurableSessionEntry> _history = [];
    private readonly ReadOnlyCollection<DurableSessionEntry> _historyView;

    /// <summary>
    /// Initializes a new <see cref="TemporalAgentSession"/> with the given session ID.
    /// Use this to reconnect to an existing agent session by its known workflow ID.
    /// </summary>
    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
        _historyView = new ReadOnlyCollection<DurableSessionEntry>(_history);
    }

    [JsonConstructor]
    internal TemporalAgentSession(TemporalAgentSessionId sessionId, AgentSessionStateBag stateBag) : base(stateBag)
    {
        this.SessionId = sessionId;
        _historyView = new ReadOnlyCollection<DurableSessionEntry>(_history);
    }

    /// <summary>Gets the Temporal agent session ID.</summary>
    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    /// <summary>
    /// The conversation history accumulated on this session, in turn order.
    /// </summary>
    /// <remarks>
    /// Internal by design. Exposing conversation content on the public surface is a separate
    /// decision that has not been made; nothing here depends on it.
    /// </remarks>
    internal IReadOnlyList<DurableSessionEntry> History => _historyView;

    /// <summary>Appends one entry to the session history. O(1).</summary>
    internal void AppendHistoryEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _history.Add(entry);
    }

    /// <summary>
    /// Replaces the session history wholesale. Used when restoring a session from a snapshot.
    /// </summary>
    internal void RestoreHistory(IEnumerable<DurableSessionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _history.Clear();
        _history.AddRange(entries);
    }

    /// <summary>
    /// Serializes this session to its <see cref="TemporalAgentSessionSnapshot"/> wire shape.
    /// </summary>
    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = ResolveSnapshotOptions(jsonSerializerOptions);

        var snapshot = new TemporalAgentSessionSnapshot
        {
            SessionId = this.SessionId.WorkflowId,
            StateBag = this.SerializeStateBag(),
            History = _history.Count > 0 ? _history.ToArray() : null,
        };

        return JsonSerializer.SerializeToElement(
            snapshot, opts.GetTypeInfo(typeof(TemporalAgentSessionSnapshot)));
    }

    /// <summary>
    /// Restores a session from its <see cref="TemporalAgentSessionSnapshot"/> wire shape.
    /// </summary>
    /// <remarks>
    /// A snapshot persisted before session-owned history existed carries no <c>history</c>
    /// member; it restores with an empty history rather than failing.
    /// </remarks>
    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = ResolveSnapshotOptions(jsonSerializerOptions);

        var snapshot = (TemporalAgentSessionSnapshot?)JsonSerializer.Deserialize(
            serializedSession, opts.GetTypeInfo(typeof(TemporalAgentSessionSnapshot)))
            ?? throw new JsonException("Session snapshot deserialized to null.");

        if (string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            throw new JsonException("Invalid or missing sessionId property.");
        }

        TemporalAgentSessionId sessionId = TemporalAgentSessionId.Parse(snapshot.SessionId);
        AgentSessionStateBag stateBag = snapshot.StateBag is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bagEl
            ? AgentSessionStateBag.Deserialize(bagEl)
            : new AgentSessionStateBag();

        var session = new TemporalAgentSession(sessionId, stateBag);

        if (snapshot.History is { Count: > 0 } history)
        {
            session.RestoreHistory(history);
        }

        return session;
    }

    /// <summary>
    /// Chooses the options the session snapshot is serialized with, falling back to
    /// <see cref="TemporalAgentJsonUtilities.DefaultOptions"/> when the caller's options cannot
    /// round-trip session history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MAF forwards whatever <see cref="JsonSerializerOptions"/> the caller handed
    /// <c>SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>. The MAF-specific
    /// <c>agent_request</c> / <c>agent_response</c> discriminators are <em>not</em> declared as
    /// <c>[JsonDerivedType]</c> attributes on <see cref="DurableSessionEntry"/> — they are grafted
    /// on at runtime by <c>TemporalAgentJsonUtilities</c>. Serializing a session whose history
    /// holds <see cref="AgentSessionRequest"/> / <see cref="AgentSessionResponse"/> entries with
    /// options lacking that modifier throws <see cref="NotSupportedException"/> ("Runtime type ...
    /// is not supported by polymorphic type"), so every turn that persisted a session would fail.
    /// </para>
    /// <para>
    /// This mattered nothing while history never crossed the wire — an empty history serializes
    /// fine under any options. It is load-bearing now, so the snapshot is written with options
    /// that are known to carry the registration. Caller options
    /// are still honoured whenever they can (they usually derive from
    /// <see cref="TemporalAgentJsonUtilities.DefaultOptions"/> and so pass this check).
    /// </para>
    /// </remarks>
    private static JsonSerializerOptions ResolveSnapshotOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        if (jsonSerializerOptions is null || ReferenceEquals(jsonSerializerOptions, TemporalAgentJsonUtilities.DefaultOptions))
        {
            return TemporalAgentJsonUtilities.DefaultOptions;
        }

        return CarriesAgentEntryPolymorphism(jsonSerializerOptions)
            ? jsonSerializerOptions
            : TemporalAgentJsonUtilities.DefaultOptions;
    }

    private static bool CarriesAgentEntryPolymorphism(JsonSerializerOptions options)
    {
        try
        {
            // JsonTypeInfo is cached per options instance, so this resolves once per distinct
            // caller-supplied options object rather than once per session serialization.
            var derivedTypes = options.GetTypeInfo(typeof(DurableSessionEntry)).PolymorphismOptions?.DerivedTypes;
            if (derivedTypes is null)
            {
                return false;
            }

            var hasRequest = false;
            var hasResponse = false;
            foreach (var derived in derivedTypes)
            {
                if (derived.DerivedType == typeof(AgentSessionRequest))
                {
                    hasRequest = true;
                }
                else if (derived.DerivedType == typeof(AgentSessionResponse))
                {
                    hasResponse = true;
                }
            }

            return hasRequest && hasResponse;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // Options that cannot produce metadata for DurableSessionEntry at all (e.g. a
            // source-gen-only context that never registered it) also cannot round-trip history.
            return false;
        }
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
            return new TemporalAgentSession(sessionId, AgentSessionStateBag.Deserialize(bagEl));
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
