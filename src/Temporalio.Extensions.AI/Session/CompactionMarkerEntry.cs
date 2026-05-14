using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Temporalio.Extensions.AI;

/// <summary>
/// A polymorphic <see cref="DurableSessionEntry"/> subtype that records a compaction event
/// in the session history. Persists the metadata needed to (a) reconstruct the post-compact
/// view on load, (b) regenerate or tombstone the marker when the underlying source entries
/// are erased for compliance, and (c) audit which strategy + model produced the rollup.
/// </summary>
/// <remarks>
/// <para>
/// Step 5 of the maf-feature-gap-analysis: <b>compaction marker</b>. The marker is an entry
/// in the durable history that replaces a contiguous run of source entries. When
/// <see cref="HistoryStore.IAgentHistoryStore.LoadAsync"/> is called with
/// <c>applyCompaction: true</c>, store implementations project the marker by collapsing the
/// referenced source IDs and prepending the rollup summary; with <c>applyCompaction: false</c>,
/// the raw entries are returned untouched (audit canonical).
/// </para>
/// <para>
/// <b>Wire-format constant.</b> The discriminator <c>"compaction-marker"</c> is embedded in
/// the workflow event history forever and must never change. The base class declares the
/// inline <c>[JsonDerivedType]</c> registration so both <c>DurableAIJsonContext</c> and
/// <c>TemporalAgentJsonUtilities</c> see the discriminator without per-context fan-out.
/// </para>
/// <para>
/// <b>Required fields rationale</b> (Cypher mitigation #1 — incomplete marker = compile
/// error rather than runtime surprise):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="CompactedMessageIds"/> — the source-entry correlation IDs the marker
///       replaces. Used by <c>CompactionAwareErasureHelper</c> to compute intersections when
///       a GDPR delete fires.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="Strategy"/> — the named strategy key that produced this marker
///       (<c>"truncation"</c>, <c>"sliding-window"</c>, <c>"summarization"</c>, or a user
///       custom). Operators need this to reason about why history looks the way it does.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ModelId"/> — the model that produced the rollup
///       (<see cref="string.Empty"/> for non-LLM strategies like truncation). Required so
///       audit logs can distinguish summarizer model from agent model.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="OriginatingTurnCorrelationIds"/> — the per-turn correlation IDs the
///       compaction collapsed across. Distinct from <see cref="CompactedMessageIds"/>: turns
///       group request/response entries; message IDs identify individual entries.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="CompactedAt"/> — the wall-clock instant when compaction ran. Aliases
///       <see cref="DurableSessionEntry.CreatedAt"/> at runtime so the on-wire JSON does not
///       carry two equivalent timestamps.
///     </description>
///   </item>
/// </list>
/// <para>
/// Compaction is a feature in active development. The exact shape may evolve in a future
/// preview tag; see <c>[Experimental("TA002")]</c>.
/// </para>
/// </remarks>
[Experimental("TA002")]
public sealed class CompactionMarkerEntry : DurableSessionEntry
{
    /// <summary>
    /// Gets the correlation IDs of the source entries this marker replaces in the post-compact
    /// projection. Read-only on the wire — markers are immutable once written.
    /// </summary>
    public required IReadOnlyList<string> CompactedMessageIds { get; init; }

    /// <summary>
    /// Gets the named compaction strategy key that produced this marker (e.g.
    /// <c>"truncation"</c>, <c>"sliding-window"</c>, <c>"summarization"</c>, or a
    /// user-registered custom strategy key).
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>
    /// Gets the model ID that produced the rollup. <see cref="string.Empty"/> for non-LLM
    /// strategies (truncation, sliding-window). Always present so audit consumers can
    /// branch on "empty == non-LLM strategy" without a separate flag.
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Gets the per-turn correlation IDs the compaction collapsed across. Each turn is one
    /// request/response pair; this is the set the marker subsumes.
    /// </summary>
    public required IReadOnlyList<string> OriginatingTurnCorrelationIds { get; init; }

    /// <summary>
    /// Gets the timestamp when this compaction was performed. Aliases
    /// <see cref="DurableSessionEntry.CreatedAt"/> — the marker's creation time IS its
    /// compaction time, and we deliberately do not duplicate the wire field.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset CompactedAt => CreatedAt;
}
