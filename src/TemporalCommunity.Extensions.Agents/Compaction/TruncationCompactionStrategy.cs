#pragma warning disable TA002 // compaction surface is experimental

using System.Diagnostics.CodeAnalysis;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Compaction;

/// <summary>
/// Compacts a session by dropping the oldest entries beyond a fixed threshold and recording
/// them as a single <see cref="CompactionMarkerEntry"/> with no rollup summary. Cheap and
/// deterministic — no LLM call, no context-window math, no per-token analysis. The
/// post-compact projection simply shows "N earlier turns were dropped" via the marker.
/// </summary>
/// <remarks>
/// <para>
/// Step 6c default thresholds:
/// </para>
/// <list type="bullet">
///   <item><description><b>Trigger</b>: entry count exceeds <see cref="TriggerEntryCount"/> (30).</description></item>
///   <item><description><b>Keep recent</b>: the most recent <see cref="KeepRecentCount"/> entries (10).</description></item>
/// </list>
/// <para>
/// On trigger, every entry older than the recent window is compacted into one marker. The
/// marker carries the source IDs (so audit canonical can find them) but no
/// <see cref="DurableSessionEntry.Messages"/> rollup — truncation is lossy by design and the
/// projected stream just sees a "(N earlier entries elided)" sentinel via the marker
/// presence.
/// </para>
/// <para>
/// Custom thresholds: instantiate with explicit ctor args and register manually via keyed DI
/// under a different key, OR subclass and override.
/// </para>
/// </remarks>
[Experimental("TA002")]
public sealed class TruncationCompactionStrategy : ICompactionStrategy
{
    /// <summary>
    /// The canonical keyed-DI name for this strategy. Step 6c pre-registers it under
    /// <see cref="TemporalAgentsRegistrar"/>.
    /// </summary>
    public const string Key = "truncation";

    /// <summary>Total session entry count that triggers compaction.</summary>
    public int TriggerEntryCount { get; }

    /// <summary>Number of most-recent entries to keep uncompacted post-trigger.</summary>
    public int KeepRecentCount { get; }

    /// <summary>
    /// Constructs the default-threshold instance (<see cref="TriggerEntryCount"/> = 30,
    /// <see cref="KeepRecentCount"/> = 10).
    /// </summary>
    public TruncationCompactionStrategy() : this(triggerEntryCount: 30, keepRecentCount: 10) { }

    /// <summary>
    /// Constructs an instance with custom thresholds.
    /// </summary>
    public TruncationCompactionStrategy(int triggerEntryCount, int keepRecentCount)
    {
        if (triggerEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerEntryCount), "Must be positive.");
        if (keepRecentCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(keepRecentCount), "Must be positive.");
        if (keepRecentCount >= triggerEntryCount)
            throw new ArgumentException(
                "keepRecentCount must be strictly less than triggerEntryCount — otherwise the trigger fires but nothing is compacted.",
                nameof(keepRecentCount));

        TriggerEntryCount = triggerEntryCount;
        KeepRecentCount = keepRecentCount;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count <= TriggerEntryCount)
        {
            return null;
        }

        // Compact everything older than the "keep recent" window. Skip:
        //   • CompactionMarkerEntry entries — re-compacting a marker would produce a
        //     marker-of-markers, which the projection logic isn't designed for.
        //   • IDs already referenced by some prior marker's CompactedMessageIds — avoids
        //     redundant marker-of-the-same-entries on every steady-state trigger.
        var alreadyCompacted = CompactionTargetFilter.CollectAlreadyCompactedIds(history);
        var targetCount = history.Count - KeepRecentCount;
        var targets = new List<string>(targetCount);
        for (int i = 0; i < targetCount; i++)
        {
            if (history[i] is CompactionMarkerEntry) continue;
            if (alreadyCompacted.Contains(history[i].CorrelationId)) continue;
            targets.Add(history[i].CorrelationId);
        }

        return targets.Count == 0 ? null : targets;
    }

    /// <inheritdoc/>
    public Task<CompactionResult> CompactAsync(
        CompactionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var marker = new CompactionMarkerEntry
        {
            CorrelationId = context.MarkerCorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [],
            CompactedMessageIds = context.TargetMessageIds,
            Strategy = Key,
            ModelId = string.Empty, // non-LLM strategy
            OriginatingTurnCorrelationIds = context.TargetMessageIds,
        };

        return Task.FromResult(new CompactionResult { Marker = marker });
    }
}
