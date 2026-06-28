#pragma warning disable TA002 // compaction surface is experimental

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.Agents.Compaction;

/// <summary>
/// Strategy that decides how a session's accumulated history is compacted. Implementations
/// are registered as keyed-DI singletons (the same pattern as
/// <see cref="TemporalCommunity.Extensions.AI.IChatClientDecorator"/>) and selected by name via
/// <c>DurableAgentBuilder.CompactionStrategyKey</c> or
/// <c>TemporalAgentsOptions.DefaultCompactionStrategy</c>.
/// </summary>
/// <remarks>
/// <para>
/// Strategies execute inside the workflow-dispatched <c>CompactHistory</c> activity (Step 6d).
/// Blocking I/O is acceptable; concurrency primitives are not — the activity scope is
/// single-threaded against this strategy instance.
/// </para>
/// <para>
/// <b>Built-in strategy keys</b> (Step 6c will pre-register these):
/// <c>"truncation"</c> — drop oldest entries beyond a threshold, marker carries no summary;
/// <c>"sliding-window"</c> — keep a fixed recent window, marker carries no summary;
/// <c>"summarization"</c> — invoke an LLM via the resolved <see cref="IChatClient"/> to
/// produce a rollup, marker carries the summary in <see cref="DurableSessionEntry.Messages"/>.
/// </para>
/// <para>
/// <b>Custom strategies.</b> Register via
/// <c>services.AddKeyedSingleton&lt;ICompactionStrategy&gt;("my-strategy", impl)</c> and
/// reference the same key via <c>agent.CompactionStrategyKey = "my-strategy"</c>. Step 6c's
/// built-in registrations use <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddKeyedSingleton"/>
/// (idempotent), so user-supplied strategies registered under the same built-in key win.
/// </para>
/// </remarks>
[Experimental("TA002")]
public interface ICompactionStrategy
{
    /// <summary>
    /// Decides whether the strategy should fire after the current step, and if so which
    /// source-entry correlation IDs it wants to compact. Called from the activity (Q2 = B)
    /// after the step's chat call completes; the result is recorded on
    /// <c>AgentStepResult.CompactionNeeded</c> + <c>CompactionTargetMessageIds</c> and the
    /// workflow uses those to decide whether to dispatch
    /// <see cref="CompactAsync"/>.
    /// </summary>
    /// <param name="history">
    /// The session's audit canonical history at the moment of evaluation — entries are in
    /// append order, including any prior <see cref="CompactionMarkerEntry"/> entries
    /// untouched.
    /// </param>
    /// <returns>
    /// The set of <see cref="DurableSessionEntry.CorrelationId"/>s the strategy wants to
    /// compact, or <see langword="null"/> when the trigger should not fire. Empty list is
    /// permitted but semantically equivalent to <see langword="null"/>.
    /// </returns>
    IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history);

    /// <summary>
    /// Compacts the given history. Returns the marker entry to write (its
    /// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> names the entries the marker
    /// subsumes; its <see cref="DurableSessionEntry.Messages"/> carries the rollup summary
    /// for strategies that produce one).
    /// </summary>
    /// <param name="context">
    /// The session state visible to the strategy at the moment of compaction: raw entries
    /// (audit canonical), the target IDs the activity-side trigger evaluator selected for
    /// compaction, and the agent/session identifiers for telemetry and chat-client
    /// resolution.
    /// </param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    /// <returns>
    /// A <see cref="CompactionResult"/> describing the marker to append and which raw entries
    /// the marker replaces in the post-compact projection.
    /// </returns>
    Task<CompactionResult> CompactAsync(
        CompactionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input handed to <see cref="ICompactionStrategy.CompactAsync"/>. Carries the strategy
/// everything it needs to make its decision without reaching into ambient state.
/// </summary>
[Experimental("TA002")]
public sealed record CompactionContext
{
    /// <summary>
    /// Audit canonical view of the session — raw entries loaded via
    /// <see cref="HistoryStore.IAgentHistoryStore.LoadAsync"/> with
    /// <c>applyCompaction: false</c>. Strategies are responsible for filtering by
    /// <see cref="TargetMessageIds"/> if they only want to operate on the trigger-selected
    /// subset.
    /// </summary>
    public required IReadOnlyList<DurableSessionEntry> RawEntries { get; init; }

    /// <summary>
    /// Correlation IDs the activity-side trigger evaluator (Step 6b) selected as compaction
    /// targets. Strategies SHOULD constrain their output to these IDs — the marker's
    /// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> must be a subset of this list.
    /// </summary>
    public required IReadOnlyList<string> TargetMessageIds { get; init; }

    /// <summary>Agent name — telemetry + log correlation.</summary>
    public required string AgentName { get; init; }

    /// <summary>Session ID (agent workflow ID).</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Pre-minted correlation ID the strategy MUST use as the new marker's
    /// <see cref="CompactionMarkerEntry.CorrelationId"/>. Supplied by the workflow via
    /// <c>Workflow.NewGuid()</c> so activity retries reproduce the same ID — preventing
    /// duplicate marker writes when the <c>CompactHistory</c> activity is retried.
    /// </summary>
    public required string MarkerCorrelationId { get; init; }

    /// <summary>
    /// Resolved primary chat client for the agent. Summarization strategies use this to
    /// produce rollup text. Truncation / sliding-window strategies ignore it.
    /// </summary>
    public required IChatClient ChatClient { get; init; }
}

/// <summary>
/// Helper for strategy implementations: collects the set of source-entry correlation IDs
/// already referenced by any <see cref="CompactionMarkerEntry"/> in the given history.
/// Built-in strategies use this to skip already-compacted IDs when selecting new compaction
/// targets — avoiding redundant marker-of-the-same-entries on every trigger.
/// </summary>
[Experimental("TA002")]
public static class CompactionTargetFilter
{
    /// <summary>
    /// Returns the set of correlation IDs that already appear in some marker's
    /// <see cref="CompactionMarkerEntry.CompactedMessageIds"/>. Useful for strategies whose
    /// trigger logic wants to constrain targets to "newly-uncompacted" entries.
    /// </summary>
    public static HashSet<string> CollectAlreadyCompactedIds(IReadOnlyList<DurableSessionEntry> history)
    {
        var compacted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in history)
        {
            if (entry is not CompactionMarkerEntry marker) continue;
            foreach (var id in marker.CompactedMessageIds)
            {
                compacted.Add(id);
            }
        }
        return compacted;
    }
}

/// <summary>
/// Result returned by an <see cref="ICompactionStrategy"/>. The
/// <see cref="HistoryStore.IAgentHistoryStore"/> consumer appends
/// <see cref="Marker"/> after the strategy returns; the marker's
/// <see cref="CompactionMarkerEntry.CompactedMessageIds"/> drives projection on subsequent
/// <c>LoadAsync(applyCompaction: true)</c> calls.
/// </summary>
[Experimental("TA002")]
public sealed record CompactionResult
{
    /// <summary>
    /// The marker entry to append to the store. Its required fields
    /// (<see cref="CompactionMarkerEntry.CompactedMessageIds"/>,
    /// <see cref="CompactionMarkerEntry.Strategy"/>, <see cref="CompactionMarkerEntry.ModelId"/>,
    /// <see cref="CompactionMarkerEntry.OriginatingTurnCorrelationIds"/>) must already be
    /// populated by the strategy. The compile-time required-field guard catches gaps in the
    /// strategy implementation.
    /// </summary>
    public required CompactionMarkerEntry Marker { get; init; }
}
