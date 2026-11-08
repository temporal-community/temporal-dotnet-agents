using System.Text.Json;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableTerminalTurnOutcomeLimits
{
    internal const int MinimumBytes = 4 * 1024;
}

internal static class DurableToolCallSummaryBudget
{
    internal const int MaximumBytes = 16 * 1024;
    internal const int MaximumCount = 32;

    internal static (
        IReadOnlyList<DurableToolCallOutcomeSummary> Summaries,
        bool Truncated,
        DurableToolCallOutcomeCounts Counts) Create(IEnumerable<DurableToolCallOutcomeSummary> calls)
    {
        var retained = new List<DurableToolCallOutcomeSummary>();
        var completed = 0;
        var failed = 0;
        var uncertain = 0;
        foreach (var call in calls)
        {
            switch (call.Status)
            {
                case DurableToolCallOutcomeStatus.Completed: completed++; break;
                case DurableToolCallOutcomeStatus.Failed: failed++; break;
                case DurableToolCallOutcomeStatus.OutcomeUncertain: uncertain++; break;
            }

            retained.Add(call);
            while (retained.Count > MaximumCount
                || JsonSerializer.SerializeToUtf8Bytes(
                    retained,
                    DurableAIJsonUtilities.DefaultOptions).Length > MaximumBytes)
            {
                retained.RemoveAt(0);
            }
        }

        return (retained, retained.Count < completed + failed + uncertain,
            new DurableToolCallOutcomeCounts(completed, failed, uncertain));
    }

    internal static DurableToolCallOutcomeCounts GetOmittedCounts(
        DurableToolCallOutcomeCounts counts,
        IReadOnlyList<DurableToolCallOutcomeSummary> summaries) =>
        new(
            counts.Completed - summaries.Count(call => call.Status == DurableToolCallOutcomeStatus.Completed),
            counts.Failed - summaries.Count(call => call.Status == DurableToolCallOutcomeStatus.Failed),
            counts.OutcomeUncertain - summaries.Count(call => call.Status == DurableToolCallOutcomeStatus.OutcomeUncertain));
}

internal sealed record DurableTurnFailureDetails
{
    public required string RequestId { get; init; }
    public required string ErrorType { get; init; }
    public required string Message { get; init; }
    public long? CommittedRevision { get; init; }
    public IReadOnlyList<DurableToolCallOutcomeSummary>? ToolCallSummaries { get; init; }
    public bool ToolCallSummariesTruncated { get; init; }
    public DurableToolCallOutcomeCounts? ToolCallCounts { get; init; }
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
