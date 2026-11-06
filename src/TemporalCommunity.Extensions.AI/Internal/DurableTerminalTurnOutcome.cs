using System.Text.Json;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableTerminalTurnOutcomeLimits
{
    internal const int MinimumBytes = 4 * 1024;
}

internal readonly record struct DurableTerminalTurnOutcomeFit(
    DurableTerminalTurnOutcome Outcome,
    int SerializedSizeBytes);

internal static class DurableTerminalTurnOutcomeBudget
{
    internal static DurableTerminalTurnOutcomeFit Fit(
        DurableTerminalTurnOutcome full,
        DurableTerminalTurnOutcome compact,
        DurableTerminalTurnOutcome minimal,
        int maximumBytes,
        JsonSerializerOptions serializerOptions)
    {
        var fullSize = JsonSerializer.SerializeToUtf8Bytes(full, serializerOptions).Length;
        if (fullSize <= maximumBytes)
        {
            return new DurableTerminalTurnOutcomeFit(full, fullSize);
        }

        var compactSize = JsonSerializer.SerializeToUtf8Bytes(compact, serializerOptions).Length;
        if (compactSize <= maximumBytes)
        {
            return new DurableTerminalTurnOutcomeFit(compact, compactSize);
        }

        var minimalSize = JsonSerializer.SerializeToUtf8Bytes(minimal, serializerOptions).Length;
        if (minimalSize <= maximumBytes)
        {
            return new DurableTerminalTurnOutcomeFit(minimal, minimalSize);
        }

        throw new InvalidOperationException(
            "The minimal terminal-outcome record exceeded its validated byte budget.");
    }
}

internal sealed record DurableTerminalTurnOutcome
{
    public required string RequestId { get; init; }

    public required string AdmissionFingerprint { get; init; }

    public string? TurnInputFingerprint { get; init; }

    public JsonElement? Output { get; init; }

    public JsonElement? ResponseEntry { get; init; }

    public DurableTerminalTurnFailure? Failure { get; init; }

    public bool OutcomeOmitted { get; init; }

    public bool IsMinimal { get; init; }

    public string? Status { get; init; }

    public long? CommittedRevision { get; init; }

    public IReadOnlyList<DurableToolCallOutcomeSummary>? ToolCallSummaries { get; init; }

    public bool ToolCallSummariesTruncated { get; init; }

    public DurableToolCallOutcomeCounts? ToolCallCounts { get; init; }
}

internal sealed record DurableTerminalTurnFailure
{
    public required string ErrorType { get; init; }

    public required string Message { get; init; }

    public required bool NonRetryable { get; init; }
}

internal sealed record DurableTurnOutcomeTooLargeDetails
{
    public required string RequestId { get; init; }

    public required string Status { get; init; }

    public bool IsMinimal { get; init; }

    public long? CommittedRevision { get; init; }

    public string? ErrorType { get; init; }

    public IReadOnlyList<DurableToolCallOutcomeSummary>? ToolCallSummaries { get; init; }

    public bool ToolCallSummariesTruncated { get; init; }

    public DurableToolCallOutcomeCounts? ToolCallCounts { get; init; }
}
