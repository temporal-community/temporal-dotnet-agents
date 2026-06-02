#pragma warning disable TA002 // compaction surface is experimental

using System.Diagnostics.CodeAnalysis;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.Compaction;

/// <summary>
/// Keeps a fixed-size recent window of session entries and compacts everything older into
/// one or more <see cref="CompactionMarkerEntry"/> entries. The trigger fires continuously
/// once the window threshold is exceeded — every new turn that pushes the count past
/// <see cref="WindowSize"/> triggers another compaction step.
/// </summary>
/// <remarks>
/// <para>
/// Step 6c default <see cref="WindowSize"/> = 20. The trigger fires every time the entry
/// count exceeds this value — yielding a steady-state stream of small markers rather than
/// the bigger one-shot compactions <see cref="TruncationCompactionStrategy"/> produces.
/// </para>
/// <para>
/// <b>Difference from truncation:</b> truncation only triggers when entries vastly exceed
/// its threshold and compacts the entire older portion in one go; sliding-window triggers
/// at every boundary crossing and compacts incrementally. Sliding-window is friendlier to
/// long-running sessions where compaction cost should be amortized; truncation is friendlier
/// to bursty workloads where compaction work should be batched.
/// </para>
/// <para>
/// Like truncation, this strategy produces no rollup summary — the marker carries source IDs
/// only. Combine with <see cref="SummarizationCompactionStrategy"/> if you need a summarized
/// rollup at every slide.
/// </para>
/// </remarks>
[Experimental("TA002")]
public sealed class SlidingWindowCompactionStrategy : ICompactionStrategy
{
    /// <summary>
    /// The canonical keyed-DI name for this strategy. Step 6c pre-registers it under
    /// <see cref="TemporalAgentsRegistrar"/>.
    /// </summary>
    public const string Key = "sliding-window";

    /// <summary>
    /// The number of most-recent entries to keep uncompacted. Each turn that pushes the
    /// total entry count past this size triggers a compaction step for the entries that
    /// fell out of the window.
    /// </summary>
    public int WindowSize { get; }

    /// <summary>Constructs the default instance (<see cref="WindowSize"/> = 20).</summary>
    public SlidingWindowCompactionStrategy() : this(windowSize: 20) { }

    /// <summary>Constructs an instance with a custom window size.</summary>
    public SlidingWindowCompactionStrategy(int windowSize)
    {
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Must be positive.");
        WindowSize = windowSize;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count <= WindowSize)
        {
            return null;
        }

        // Compact only entries beyond the window that aren't already referenced by a prior
        // marker. The CompactionTargetFilter skip means steady-state sliding only emits one
        // marker per new-entry batch, not a redundant marker-over-marker on every turn.
        var alreadyCompacted = CompactionTargetFilter.CollectAlreadyCompactedIds(history);
        var compactCount = history.Count - WindowSize;
        var targets = new List<string>(compactCount);
        for (int i = 0; i < compactCount; i++)
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
            ModelId = string.Empty,
            OriginatingTurnCorrelationIds = context.TargetMessageIds,
        };

        return Task.FromResult(new CompactionResult { Marker = marker });
    }
}
