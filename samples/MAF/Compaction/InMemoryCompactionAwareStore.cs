using System.Collections.Concurrent;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Agents.HistoryStore;

namespace Compaction;

/// <summary>
/// Reference <see cref="IAgentHistoryStore"/> implementation with full compaction-marker
/// awareness. Demonstrates the projection contract for the two <c>applyCompaction</c> modes:
///
///   • <c>applyCompaction: false</c> → raw entries (audit canonical view).
///   • <c>applyCompaction: true</c>  → project markers: source IDs each marker references
///                                     are filtered out; the marker itself stays (carrying
///                                     the rollup summary, if any).
///
/// Real-world stores follow the same shape — only the IO layer (Cosmos DB, PostgreSQL, etc.)
/// differs.
/// </summary>
public sealed class InMemoryCompactionAwareStore : IAgentHistoryStore
{
    private readonly ConcurrentDictionary<string, List<DurableSessionEntry>> _store = new();

    public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId,
        bool applyCompaction,
        CancellationToken cancellationToken = default)
    {
        if (!_store.TryGetValue(sessionId, out var bucket))
        {
            return Task.FromResult<IReadOnlyList<DurableSessionEntry>>([]);
        }

        DurableSessionEntry[] raw;
        lock (bucket)
        {
            raw = bucket.ToArray();
        }

        return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(
            applyCompaction ? Project(raw) : raw);
    }

    public Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var bucket = _store.GetOrAdd(sessionId, _ => new());
        lock (bucket)
        {
            // Idempotency: dedupe on (CorrelationId, type) so activity retries don't
            // double-write. Request/response entries share a CorrelationId by design (they
            // are a pair) but differ on type; a request OR a response retry is a no-op.
            // Production stores typically use a (sessionId, correlationId, type) composite
            // primary key with INSERT ... ON CONFLICT DO NOTHING.
            var existing = new HashSet<(string, string)>(
                bucket.Select(e => (e.CorrelationId, e.GetType().Name)));
            foreach (var entry in entries)
            {
                var key = (entry.CorrelationId, entry.GetType().Name);
                if (existing.Add(key))
                {
                    bucket.Add(entry);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> reducedEntries,
        CancellationToken cancellationToken = default)
    {
        var bucket = _store.GetOrAdd(sessionId, _ => new());
        lock (bucket)
        {
            bucket.Clear();
            bucket.AddRange(reducedEntries);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Audit canonical accessor for the demo driver. Bypasses projection so the verification
    /// output can show the full append history.
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> SnapshotRaw(string sessionId)
    {
        if (!_store.TryGetValue(sessionId, out var bucket)) return [];
        lock (bucket)
        {
            return bucket.ToArray();
        }
    }

    /// <summary>
    /// Projected-view accessor for the demo driver. Same logic the LLM-facing
    /// <see cref="LoadAsync"/> call uses.
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> SnapshotProjected(string sessionId)
    {
        var raw = SnapshotRaw(sessionId);
        return raw.Count == 0 ? raw : Project(raw);
    }

    // -------------------------------------------------------------------------
    // Projection logic — collapse each marker's CompactedMessageIds. Raise
    // DurableCompactionMarkerException if a marker references an entry no longer
    // in the store (out-of-band erasure bypass) per Cypher mitigation #3.
    // -------------------------------------------------------------------------

    private static IReadOnlyList<DurableSessionEntry> Project(IReadOnlyList<DurableSessionEntry> raw)
    {
        // Build the set of all present IDs for fast presence checks during marker validation.
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in raw)
        {
            presentIds.Add(entry.CorrelationId);
        }

        // Walk markers: validate references + collect the union of compacted IDs to filter.
        HashSet<string>? compacted = null;
        foreach (var entry in raw)
        {
            if (entry is not CompactionMarkerEntry marker) continue;

            compacted ??= new HashSet<string>(StringComparer.Ordinal);

            List<string>? missing = null;
            foreach (var id in marker.CompactedMessageIds)
            {
                if (!presentIds.Contains(id))
                {
                    (missing ??= new List<string>()).Add(id);
                }
                compacted.Add(id);
            }

            if (missing is not null)
            {
                throw new DurableCompactionMarkerException(
                    marker.CorrelationId,
                    $"Marker '{marker.CorrelationId}' (strategy={marker.Strategy}) references " +
                    $"source IDs that are not present in the store. Run " +
                    $"CompactionAwareErasureHelper.EraseSessionDataAsync to regenerate / tombstone " +
                    $"affected markers when entries are erased.",
                    missing);
            }
        }

        if (compacted is null) return raw;

        var projected = new List<DurableSessionEntry>(raw.Count);
        foreach (var entry in raw)
        {
            // Markers ALWAYS stay in the projected view — their Messages carry the rollup
            // summary that replaces the collapsed source entries.
            if (entry is CompactionMarkerEntry)
            {
                projected.Add(entry);
                continue;
            }

            // Non-marker entries that some marker subsumes are filtered out.
            if (compacted.Contains(entry.CorrelationId)) continue;

            projected.Add(entry);
        }

        return projected;
    }
}
