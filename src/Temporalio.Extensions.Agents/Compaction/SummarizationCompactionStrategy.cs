#pragma warning disable TA002 // compaction surface is experimental

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.Compaction;

/// <summary>
/// Compacts the session by invoking an LLM to produce a rollup summary of the entries
/// beyond the keep-recent window. The marker carries the summary in its
/// <see cref="DurableSessionEntry.Messages"/> field — so the post-compact projection
/// shows "summary of N earlier turns" in place of the collapsed entries.
/// </summary>
/// <remarks>
/// <para>
/// Step 6c default thresholds:
/// </para>
/// <list type="bullet">
///   <item><description><b>Trigger</b>: entry count exceeds <see cref="TriggerEntryCount"/> (30).</description></item>
///   <item><description><b>Keep recent</b>: <see cref="KeepRecentCount"/> (10).</description></item>
/// </list>
/// <para>
/// <b>LLM dispatch.</b> The strategy invokes the resolved
/// <see cref="CompactionContext.ChatClient"/> inline within <see cref="CompactAsync"/>. The
/// strategy itself runs inside an activity (the <c>CompactHistory</c> dispatch added in
/// Step 6d) so blocking I/O is fine. Per Q6, this preserves the "one LLM call = one
/// activity" invariant — the compaction activity hosts the LLM call directly.
/// </para>
/// <para>
/// The system prompt (<see cref="SystemPrompt"/>) is conservative by default. Users wanting
/// domain-specific summarization should register a custom strategy with a tailored prompt.
/// </para>
/// </remarks>
[Experimental("TA002")]
public sealed class SummarizationCompactionStrategy : ICompactionStrategy
{
    /// <summary>
    /// The canonical keyed-DI name for this strategy. Step 6c pre-registers it under
    /// <see cref="TemporalAgentsRegistrar"/>.
    /// </summary>
    public const string Key = "summarization";

    /// <summary>Total session entry count that triggers compaction.</summary>
    public int TriggerEntryCount { get; }

    /// <summary>Number of most-recent entries to keep uncompacted post-trigger.</summary>
    public int KeepRecentCount { get; }

    /// <summary>System prompt prepended to the summarization request.</summary>
    public string SystemPrompt { get; }

    /// <summary>Constructs the default instance (trigger=30, keepRecent=10, conservative prompt).</summary>
    public SummarizationCompactionStrategy()
        : this(triggerEntryCount: 30, keepRecentCount: 10, systemPrompt: DefaultSystemPrompt) { }

    /// <summary>Constructs an instance with custom thresholds and prompt.</summary>
    public SummarizationCompactionStrategy(int triggerEntryCount, int keepRecentCount, string systemPrompt)
    {
        if (triggerEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerEntryCount), "Must be positive.");
        if (keepRecentCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(keepRecentCount), "Must be positive.");
        if (keepRecentCount >= triggerEntryCount)
            throw new ArgumentException(
                "keepRecentCount must be strictly less than triggerEntryCount.",
                nameof(keepRecentCount));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        TriggerEntryCount = triggerEntryCount;
        KeepRecentCount = keepRecentCount;
        SystemPrompt = systemPrompt;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count <= TriggerEntryCount)
        {
            return null;
        }

        var targetCount = history.Count - KeepRecentCount;
        var targets = new List<string>(targetCount);
        for (int i = 0; i < targetCount; i++)
        {
            if (history[i] is CompactionMarkerEntry) continue;
            targets.Add(history[i].CorrelationId);
        }

        return targets.Count == 0 ? null : targets;
    }

    /// <inheritdoc/>
    public async Task<CompactionResult> CompactAsync(
        CompactionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Build the summarization prompt: system instruction + the messages from the
        // target entries. Filter to entries whose CorrelationId is in TargetMessageIds.
        var targetIds = new HashSet<string>(context.TargetMessageIds, StringComparer.Ordinal);
        var prompt = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
        };
        foreach (var entry in context.RawEntries)
        {
            if (!targetIds.Contains(entry.CorrelationId)) continue;
            foreach (var m in entry.Messages)
            {
                prompt.Add(m);
            }
        }

        var response = await context.ChatClient
            .GetResponseAsync(prompt, options: null, cancellationToken)
            .ConfigureAwait(false);

        var summaryMessages = response.Messages.Count > 0
            ? response.Messages.ToArray()
            : new[] { new ChatMessage(ChatRole.Assistant, "(no rollup produced)") };

        var marker = new CompactionMarkerEntry
        {
            CorrelationId = context.MarkerCorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = summaryMessages,
            CompactedMessageIds = context.TargetMessageIds,
            Strategy = Key,
            ModelId = response.ModelId ?? string.Empty,
            OriginatingTurnCorrelationIds = context.TargetMessageIds,
        };

        return new CompactionResult { Marker = marker };
    }

    /// <summary>Default system prompt — conservative, domain-agnostic.</summary>
    public const string DefaultSystemPrompt =
        "You are a summarizer. Produce a concise, factually accurate rollup of the conversation " +
        "below so a downstream assistant can continue the dialogue with the gist preserved. " +
        "Do NOT invent facts. Preserve specific names, numbers, decisions, and unresolved questions. " +
        "Keep the summary under 250 words.";
}
