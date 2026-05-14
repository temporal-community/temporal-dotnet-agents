#pragma warning disable TA002 // compaction surface is experimental

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.Compaction;

/// <summary>
/// Strategy that decides how a session's accumulated history is compacted. Implementations
/// are registered as keyed-DI singletons (the same pattern as
/// <see cref="Temporalio.Extensions.AI.IChatClientDecorator"/>) and selected by name via
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
    /// Resolved primary chat client for the agent. Summarization strategies use this to
    /// produce rollup text. Truncation / sliding-window strategies ignore it.
    /// </summary>
    public required IChatClient ChatClient { get; init; }
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
