#pragma warning disable TA002 // test helper consumes the experimental compaction surface
using System.Collections.Concurrent;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using TemporalCommunity.Extensions.Agents.HistoryStore;

namespace TemporalCommunity.Extensions.Agents.Tests.HistoryStore;

/// <summary>
/// In-memory test double for <see cref="IAgentHistoryStore"/>. Records every call so tests
/// can assert ordering, inputs, and call counts.
/// </summary>
/// <remarks>
/// <para>
/// All operations are concurrent-safe. Per-session storage is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>; per-session entry lists are
/// guarded by per-session locks to preserve append ordering for assertions.
/// </para>
/// </remarks>
internal sealed class FakeAgentHistoryStore : IAgentHistoryStore
{
    private readonly ConcurrentDictionary<string, List<DurableSessionEntry>> _store = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedCall> _calls = new();

    /// <summary>Recorded operations against the store, in chronological order.</summary>
    public IReadOnlyCollection<RecordedCall> Calls => _calls.ToArray();

    /// <summary>Number of times <see cref="LoadAsync"/> has been called.</summary>
    public int LoadCount => _calls.Count(c => c.Operation == HistoryStoreOperation.Load);

    /// <summary>Number of times <see cref="AppendAsync"/> has been called.</summary>
    public int AppendCount => _calls.Count(c => c.Operation == HistoryStoreOperation.Append);

    /// <summary>Number of times <see cref="ReplaceAsync"/> has been called.</summary>
    public int ReplaceCount => _calls.Count(c => c.Operation == HistoryStoreOperation.Replace);

    /// <summary>
    /// Optional hook to inject an exception on the next call of any kind. Useful
    /// for testing how the workflow / activity surfaces store failures.
    /// </summary>
    public Func<HistoryStoreOperation, string, Exception?>? FailureInjector { get; set; }

    /// <summary>
    /// Optional hook to delay or yield before each operation completes — used to
    /// surface ordering bugs when concurrent turns race against the mutex.
    /// </summary>
    public Func<HistoryStoreOperation, string, Task>? PreOperationHook { get; set; }

    public async Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId,
        bool applyCompaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        await PreOpAsync(HistoryStoreOperation.Load, sessionId).ConfigureAwait(false);

        _calls.Enqueue(new RecordedCall(
            HistoryStoreOperation.Load,
            sessionId,
            Entries: null,
            Timestamp: DateTimeOffset.UtcNow));

        if (!_store.TryGetValue(sessionId, out var list))
            return [];

        DurableSessionEntry[] raw;
        lock (GetLock(sessionId))
        {
            raw = list.ToArray();
        }

        return applyCompaction ? ProjectCompaction(raw) : raw;
    }

    /// <summary>
    /// Step 5b minimal projection: for each <see cref="CompactionMarkerEntry"/>, drop the
    /// source entries it references from the output stream. The marker itself stays in the
    /// stream (its <c>Messages</c> field carries the rollup summary). Cypher mitigation #3
    /// — if any referenced source ID is missing from the store, surface
    /// <see cref="DurableCompactionMarkerException"/> rather than silently producing a
    /// misleading projection.
    /// </summary>
    private static IReadOnlyList<DurableSessionEntry> ProjectCompaction(
        IReadOnlyList<DurableSessionEntry> raw)
    {
        // Pass 1: collect the union of all CompactedMessageIds + validate each marker's
        // references exist somewhere in the stream.
        HashSet<string>? compacted = null;
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in raw)
        {
            presentIds.Add(entry.CorrelationId);
        }

        foreach (var entry in raw)
        {
            if (entry is not CompactionMarkerEntry marker)
                continue;

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
                    $"source entry IDs that are not present in the store. This usually means an " +
                    $"out-of-band erasure removed entries without going through " +
                    $"CompactionAwareErasureHelper. Run the erasure helper to tombstone or " +
                    $"regenerate the marker.",
                    missing);
            }
        }

        if (compacted is null)
            return raw;

        var projected = new List<DurableSessionEntry>(raw.Count);
        foreach (var entry in raw)
        {
            if (entry is CompactionMarkerEntry)
            {
                // Markers always stay in the projected stream — their Messages field holds
                // the rollup summary that replaces the collapsed source entries.
                projected.Add(entry);
                continue;
            }

            if (compacted.Contains(entry.CorrelationId))
                continue;

            projected.Add(entry);
        }

        return projected;
    }

    public async Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(entries);
        await PreOpAsync(HistoryStoreOperation.Append, sessionId).ConfigureAwait(false);

        _calls.Enqueue(new RecordedCall(
            HistoryStoreOperation.Append,
            sessionId,
            Entries: [.. entries],
            Timestamp: DateTimeOffset.UtcNow));

        var bucket = _store.GetOrAdd(sessionId, _ => []);
        lock (GetLock(sessionId))
        {
            bucket.AddRange(entries);
        }
    }

    public async Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> reducedEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(reducedEntries);
        await PreOpAsync(HistoryStoreOperation.Replace, sessionId).ConfigureAwait(false);

        _calls.Enqueue(new RecordedCall(
            HistoryStoreOperation.Replace,
            sessionId,
            Entries: [.. reducedEntries],
            Timestamp: DateTimeOffset.UtcNow));

        var bucket = _store.GetOrAdd(sessionId, _ => []);
        lock (GetLock(sessionId))
        {
            bucket.Clear();
            bucket.AddRange(reducedEntries);
        }
    }

    /// <summary>Test-only synchronous accessor for assertions.</summary>
    public IReadOnlyList<DurableSessionEntry> Snapshot(string sessionId)
    {
        if (!_store.TryGetValue(sessionId, out var bucket))
            return [];
        lock (GetLock(sessionId))
        {
            return bucket.ToArray();
        }
    }

    /// <summary>Pre-seeds entries for a session — simulates a non-empty store on first turn.</summary>
    public void Seed(string sessionId, IEnumerable<DurableSessionEntry> entries)
    {
        var bucket = _store.GetOrAdd(sessionId, _ => []);
        lock (GetLock(sessionId))
        {
            bucket.AddRange(entries);
        }
    }

    private async Task PreOpAsync(HistoryStoreOperation op, string sessionId)
    {
        if (PreOperationHook is { } hook)
            await hook(op, sessionId).ConfigureAwait(false);

        if (FailureInjector?.Invoke(op, sessionId) is { } ex)
            throw ex;
    }

    private object GetLock(string sessionId) =>
        _locks.GetOrAdd(sessionId, _ => new object());

    /// <summary>Tagged record of a single store operation for test assertions.</summary>
    internal sealed record RecordedCall(
        HistoryStoreOperation Operation,
        string SessionId,
        IReadOnlyList<DurableSessionEntry>? Entries,
        DateTimeOffset Timestamp);
}

internal enum HistoryStoreOperation
{
    Load,
    Append,
    Replace,
}
