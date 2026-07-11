#pragma warning disable TA002 // helper consumes the experimental compaction surface

using System.Diagnostics.CodeAnalysis;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.HistoryStore;

/// <summary>
/// Static utility that performs a compaction-aware erasure cascade against an
/// <see cref="IAgentHistoryStore"/>. Required helper for any GDPR / right-to-be-forgotten
/// workflow that needs to remove specific entries from a session history that may contain
/// <see cref="CompactionMarkerEntry"/> entries.
/// </summary>
/// <remarks>
/// <para>
/// Step 5c (Cypher mitigation #2). The naive approach — "load, filter by ID, replace" —
/// silently corrupts the store when a marker references an erased source: the marker's
/// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> would still name the erased ID,
/// so on the next <c>LoadAsync(applyCompaction: true)</c> the projection-validation guard
/// (Cypher mitigation #3) would surface a <c>DurableCompactionMarkerException</c>, breaking
/// the agent's next inference call. This helper walks markers first and decides per-marker
/// whether to <b>tombstone</b> (remove entirely) or <b>regenerate</b> (rewrite with the
/// surviving subset) per Q12.
/// </para>
/// <para>
/// <b>Tombstone rule</b> (all of a marker's <c>CompactedMessageIds</c> are erased):
/// remove the marker entirely. The surviving entries the marker subsumed are zero — there
/// is nothing left to summarize. If the marker carried a rollup summary in
/// <see cref="DurableSessionEntry.Messages"/>, that summary may contain text derived from
/// the erased content; removing it eliminates the leak.
/// </para>
/// <para>
/// <b>Regenerate rule</b> (some but not all of a marker's <c>CompactedMessageIds</c> are
/// erased): rewrite the marker with <c>CompactedMessageIds</c> reduced to the surviving
/// subset, and <b>clear</b> <see cref="DurableSessionEntry.Messages"/> — the rollup summary
/// may contain content derived from the erased entries, so it is no longer safe to project.
/// On the next compaction cycle the strategy will re-summarize against the surviving entries
/// and produce a fresh marker.
/// </para>
/// <para>
/// All work is bracketed in a single <c>LoadAsync</c> → <c>ReplaceAsync</c> round trip.
/// Concurrency is up to the store implementation; helper does not lock.
/// </para>
/// </remarks>
[Experimental("TA002")]
public static class CompactionAwareErasureHelper
{
    /// <summary>
    /// Performs a compaction-aware erasure cascade against the store.
    /// </summary>
    /// <param name="store">The store to erase from. Must not be <see langword="null"/>.</param>
    /// <param name="sessionId">The session whose history is being modified.</param>
    /// <param name="erasedMessageIds">
    /// Correlation IDs of the entries to erase from the store. Entries whose
    /// <see cref="DurableSessionEntry.CorrelationId"/> is in this set are dropped from the
    /// output; markers whose <see cref="CompactionMarkerEntry.CompactedMessageIds"/>
    /// reference any of these are tombstoned or regenerated per the rules above.
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// An <see cref="EraseResult"/> recording how many markers were tombstoned, how many were
    /// regenerated, and how many source entries remain post-erasure. Useful for audit
    /// reports and compliance attestations.
    /// </returns>
    public static async Task<EraseResult> EraseSessionDataAsync(
        IAgentHistoryStore store,
        string sessionId,
        IEnumerable<string> erasedMessageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(erasedMessageIds);

        // Use a common, immutable-at-the-boundary contract across both target assets. The
        // implementation needs set lookup semantics, so materialize once instead of exposing
        // IReadOnlySet<T> (net5+) on one TFM and ISet<T> on the other.
        var erasedSet = new HashSet<string>(erasedMessageIds);

        // 1) Load raw (audit canonical) view — the only mode that surfaces all entries
        //    including markers untouched.
        var raw = await store
            .LoadAsync(sessionId, applyCompaction: false, cancellationToken)
            .ConfigureAwait(false);

        if (raw.Count == 0)
        {
            return new EraseResult
            {
                MarkersAffected = 0,
                MarkersTombstoned = 0,
                MarkersRegenerated = 0,
                RemainingMessageCount = 0,
            };
        }

        var rewritten = new List<DurableSessionEntry>(raw.Count);
        int markersAffected = 0;
        int markersTombstoned = 0;
        int markersRegenerated = 0;

        foreach (var entry in raw)
        {
            if (entry is CompactionMarkerEntry marker)
            {
                var surviving = ComputeSurvivors(marker.CompactedMessageIds, erasedSet);

                if (surviving.Count == marker.CompactedMessageIds.Count)
                {
                    // No overlap with the erasure set — pass the marker through unchanged.
                    rewritten.Add(marker);
                    continue;
                }

                markersAffected++;

                if (surviving.Count == 0)
                {
                    // Tombstone: every source ID was erased. Removing the marker eliminates
                    // any rollup-summary leakage and aligns the projection-validation guard.
                    markersTombstoned++;
                    continue;
                }

                // Regenerate: rewrite with surviving IDs + clear the rollup summary
                // (Messages) so erased-derived content does not project on next load.
                markersRegenerated++;
                rewritten.Add(new CompactionMarkerEntry
                {
                    CorrelationId = marker.CorrelationId,
                    CreatedAt = marker.CreatedAt,
                    Messages = [],
                    CompactedMessageIds = surviving,
                    Strategy = marker.Strategy,
                    ModelId = marker.ModelId,
                    // Apply the same survivor filter to the originating-turn IDs so the
                    // rewritten marker does not retain references to erased turns
                    // (store-consistency after GDPR erasure).
                    OriginatingTurnCorrelationIds =
                        ComputeSurvivors(marker.OriginatingTurnCorrelationIds, erasedSet),
                });
                continue;
            }

            // Non-marker: drop if its correlation ID is in the erasure set; otherwise keep.
            if (erasedSet.Contains(entry.CorrelationId))
            {
                continue;
            }

            rewritten.Add(entry);
        }

        await store.ReplaceAsync(sessionId, rewritten, cancellationToken).ConfigureAwait(false);

        return new EraseResult
        {
            MarkersAffected = markersAffected,
            MarkersTombstoned = markersTombstoned,
            MarkersRegenerated = markersRegenerated,
            RemainingMessageCount = rewritten.Count,
        };
    }

    private static IReadOnlyList<string> ComputeSurvivors(
        IReadOnlyList<string> compactedIds,
        ISet<string> erased)
    {
        // Fast path: no overlap — return the original list, no allocation.
        bool anyOverlap = false;
        for (int i = 0; i < compactedIds.Count; i++)
        {
            if (erased.Contains(compactedIds[i]))
            {
                anyOverlap = true;
                break;
            }
        }
        if (!anyOverlap) return compactedIds;

        var survivors = new List<string>(compactedIds.Count);
        for (int i = 0; i < compactedIds.Count; i++)
        {
            if (!erased.Contains(compactedIds[i]))
            {
                survivors.Add(compactedIds[i]);
            }
        }
        return survivors;
    }
}

/// <summary>
/// Summary of the work <see cref="CompactionAwareErasureHelper.EraseSessionDataAsync"/>
/// performed against the store.
/// </summary>
[Experimental("TA002")]
public sealed record EraseResult
{
    /// <summary>
    /// Number of <see cref="CompactionMarkerEntry"/> entries whose
    /// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> overlapped with the erasure
    /// set (sum of <see cref="MarkersTombstoned"/> + <see cref="MarkersRegenerated"/>).
    /// </summary>
    public required int MarkersAffected { get; init; }

    /// <summary>
    /// Number of markers removed entirely because every source ID they referenced was in
    /// the erasure set.
    /// </summary>
    public required int MarkersTombstoned { get; init; }

    /// <summary>
    /// Number of markers rewritten with a reduced <see cref="CompactionMarkerEntry.CompactedMessageIds"/>
    /// set and cleared <see cref="DurableSessionEntry.Messages"/>.
    /// </summary>
    public required int MarkersRegenerated { get; init; }

    /// <summary>
    /// Total number of entries (markers + sources) remaining in the store after the cascade.
    /// </summary>
    public required int RemainingMessageCount { get; init; }
}
