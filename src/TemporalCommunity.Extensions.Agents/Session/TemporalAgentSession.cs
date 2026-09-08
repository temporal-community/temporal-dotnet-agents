using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Workflows;
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
    // Derived options per caller-supplied instance, for the ResolveSnapshotOptions fallback.
    // Holds no strong reference to the caller's options.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions>
        FallbackOptionsCache = new();

    // Mutable backing store so AppendHistoryEntry stays O(1). _historyView is created once (field
    // initializers run in declaration order, so _history is already assigned) and handed out
    // instead of _history itself, so callers cannot cast the facade back to a mutable list.
    private readonly List<DurableSessionEntry> _history = [];
    private readonly ReadOnlyCollection<DurableSessionEntry> _historyView;
    private bool _runInProgress;

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

    /// <summary>
    /// Marks this session as having a run in flight, rejecting a second overlapping run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flag lives on the session, not on the agent: one agent instance is expected to drive
    /// several distinct sessions at once, and blocking that would defeat the point of session
    /// ownership. What is not safe is two runs interleaving over the <em>same</em> session, since
    /// they would append to one history and merge into one StateBag with no defined ordering.
    /// </para>
    /// <para>
    /// Fail fast rather than serialize the second caller: silently queueing would hide a
    /// programming error behind non-deterministic turn ordering.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A run is already in progress on this session.</exception>
    internal void EnterRun()
    {
        if (_runInProgress)
        {
            throw new InvalidOperationException(
                "Overlapping RunAsync() calls on the same session are not allowed. " +
                "Use distinct session objects for parallel conversations.");
        }

        _runInProgress = true;
        this.RunCount++;
    }

    /// <summary>
    /// How many runs this session has started. Used for log correlation only.
    /// </summary>
    /// <remarks>
    /// Per session rather than per agent: one agent drives many conversations, so an agent-level
    /// counter would report session B's first turn as turn 3. Not part of the snapshot — a session
    /// restored after continue-as-new counts from zero again, matching the pre-existing behaviour
    /// of the agent-level counter it replaced.
    /// </remarks>
    internal int RunCount { get; private set; }

    /// <summary>
    /// Clears the in-flight marker. Must run on every completion, cancellation, and failure path.
    /// </summary>
    internal void ExitRun() => _runInProgress = false;

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
    /// that are known to carry the registration. Caller options are used as given whenever they
    /// can round-trip history (they usually derive from
    /// <see cref="TemporalAgentJsonUtilities.DefaultOptions"/> and so pass this check).
    /// </para>
    /// <para>
    /// <strong>When a fallback is needed, the caller's security-relevant settings are carried
    /// over.</strong> Silently swapping in the default options would also swap in their
    /// <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> encoder
    /// and default <see cref="JsonSerializerOptions.MaxDepth"/>. A caller who set a stricter
    /// encoder to make output safe to embed, or a depth limit to bound parsing of untrusted
    /// persisted state, would lose that protection without being told — and the payload now
    /// carries model- and tool-authored conversation text. <see cref="JsonSerializerOptions.Encoder"/>
    /// and <see cref="JsonSerializerOptions.MaxDepth"/> are therefore copied onto the derived
    /// options. Settings that would change the wire shape itself (naming policy, reference
    /// handling, the resolver chain) are deliberately not copied — the snapshot format is this
    /// library's contract, not the caller's.
    /// </para>
    /// </remarks>
    private static JsonSerializerOptions ResolveSnapshotOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        if (jsonSerializerOptions is null || ReferenceEquals(jsonSerializerOptions, TemporalAgentJsonUtilities.DefaultOptions))
        {
            return TemporalAgentJsonUtilities.DefaultOptions;
        }

        if (CarriesAgentEntryPolymorphism(jsonSerializerOptions))
        {
            return jsonSerializerOptions;
        }

        // Cached per caller-options instance: System.Text.Json caches resolved JsonTypeInfo on the
        // options object, so building a fresh instance per call would re-resolve all metadata every
        // time. The table holds no strong reference to the caller's options.
        return FallbackOptionsCache.GetValue(jsonSerializerOptions, static callerOptions =>
        {
            var derived = new JsonSerializerOptions(TemporalAgentJsonUtilities.DefaultOptions)
            {
                Encoder = callerOptions.Encoder,
                MaxDepth = callerOptions.MaxDepth,
            };
            derived.MakeReadOnly();
            return derived;
        });
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

    /// <summary>
    /// Applies <em>trusted</em> StateBag output returned by an LLM-step activity — context-provider
    /// mutations — on top of this session's bag, per-key, preserving keys the step did not touch.
    /// </summary>
    /// <remarks>
    /// Unfiltered by design: context providers are developer-registered and carry the same trust as
    /// the workflow thread. Tool and interceptor write-backs are untrusted and must go through
    /// <see cref="MergeToolStateBagWriteBacks"/> instead. See
    /// <see cref="StateBagMerge.OverlayTrustedStateBag"/>.
    /// </remarks>
    internal void OverlayTrustedStateBag(JsonElement? updated)
    {
        // A hash-gated or empty step returns no bag. Skipping here avoids a pointless
        // serialize/deserialize round-trip of the whole bag and matches
        // StateBagMerge.OverlayTrustedStateBag's "return current unchanged" behaviour.
        if (updated is not { ValueKind: JsonValueKind.Object })
        {
            return;
        }

        ReplaceStateBag(StateBagMerge.OverlayTrustedStateBag(this.SerializeStateBag(), updated));
    }

    /// <summary>
    /// Merges <em>untrusted</em> tool and interceptor StateBag write-backs into this session's bag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="writeBacks"/> must be ordered by original tool-call index, not by activity
    /// completion order: tool activities fan out concurrently, so completion order is
    /// non-deterministic and using it would break workflow replay. The merge applies contributions
    /// in the supplied index order, so a later index wins a top-level key conflict.
    /// </para>
    /// <para>
    /// Reserved approval-scope keys are dropped from every contribution — see
    /// <see cref="StateBagMerge.Merge"/>.
    /// </para>
    /// </remarks>
    internal void MergeToolStateBagWriteBacks(
        IReadOnlyList<JsonElement?> writeBacks,
        string? alwaysScopesStoreKey = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(writeBacks);

        var hasContribution = false;
        for (var i = 0; i < writeBacks.Count; i++)
        {
            if (writeBacks[i] is { ValueKind: JsonValueKind.Object })
            {
                hasContribution = true;
                break;
            }
        }

        if (!hasContribution)
        {
            return;
        }

        ReplaceStateBag(StateBagMerge.Merge(
            this.SerializeStateBag(), writeBacks, alwaysScopesStoreKey, logger));
    }

    /// <summary>
    /// Replaces this session's <see cref="AgentSession.StateBag"/> with the merge result. A null,
    /// <see cref="JsonValueKind.Undefined"/>, or JSON-null value clears the bag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ordinal-sorted key order produced by <see cref="StateBagMerge"/> ends here.</strong>
    /// <c>StateBagMerge.SerializeSorted</c> emits keys ordinal-sorted so that <c>AgentWorkflow</c>'s
    /// FNV-1a content hash in <c>GetStateBagForDispatch</c> sees byte-stable input. Deserializing
    /// into an <see cref="AgentSessionStateBag"/> discards that order: the bag is backed by a
    /// <c>ConcurrentDictionary</c>, so the next <see cref="SerializeStateBag"/> emits bucket order
    /// instead.
    /// </para>
    /// <para>
    /// That is harmless today — <see cref="TemporalAIAgent"/> has no hash gate, and bucket order is
    /// itself stable across processes and replays because the dictionary uses the non-randomized
    /// ordinal comparer (measured, not assumed). Replay determinism needs stability, which holds;
    /// it does not need sortedness. But the two agent loops are documented as mirrors, so if a hash
    /// gate is ever added to this path, re-sort on the way out rather than assuming the order
    /// survived the round trip.
    /// </para>
    /// </remarks>
    private void ReplaceStateBag(JsonElement? merged)
    {
        this.StateBag = merged is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } el
            ? AgentSessionStateBag.Deserialize(el)
            : new AgentSessionStateBag();
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
