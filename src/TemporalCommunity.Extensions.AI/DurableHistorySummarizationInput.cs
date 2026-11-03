using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// A complete logical turn supplied to the library-owned history summarizer.
/// </summary>
public sealed class DurableHistorySummaryTurn
{
    /// <summary>The stable identifier of the logical turn.</summary>
    public required string TurnId { get; init; }

    /// <summary>All retained messages for the turn, including completed tool exchanges.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Whether this turn is protected from summarization, such as an active, approval,
    /// instruction, or interrupted turn. Protected turns are rejected rather than summarized.
    /// </summary>
    public bool IsProtected { get; init; }
}

/// <summary>
/// Input to the activity-backed library history summarizer.
/// </summary>
public sealed class DurableHistorySummarizationInput
{
    /// <summary>Complete turns in chronological order, from oldest to newest.</summary>
    public required IReadOnlyList<DurableHistorySummaryTurn> Turns { get; init; }

    /// <summary>Optional keyed worker registration for the summarizer chat client.</summary>
    public string? ClientKey { get; init; }

    /// <summary>The total summarizer context budget, including the output reserve.</summary>
    public int MaxContextTokens { get; init; } = 8192;

    /// <summary>Tokens reserved for the summary response.</summary>
    public int OutputTokenReserve { get; init; } = 1024;

    /// <summary>Whether to preflight the complete activity input and result payloads.</summary>
    public bool EnableTransportPayloadGuard { get; init; }

    /// <summary>Maximum serialized payload size before the reserved codec expansion allowance.</summary>
    public int MaximumTransportPayloadSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Reserved bytes for payload-codec expansion.</summary>
    public int PayloadCodecExpansionAllowanceBytes { get; init; }
}

/// <summary>
/// Summary text and the number of oldest complete turns represented by it.
/// </summary>
public sealed class DurableHistorySummarizationResult
{
    /// <summary>The generated summary and provider metadata.</summary>
    public required DurableSessionSummary Summary { get; init; }

    /// <summary>The number of leading turns selected to fit the summarizer's input budget.</summary>
    public required int SummarizedTurnCount { get; init; }
}
