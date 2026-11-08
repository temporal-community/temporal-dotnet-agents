using System.Text.Json;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableTerminalOutcomeTests
{
    [Fact]
    public void SummaryBudgetKeepsNewestCallsAndAccountsForEveryOmittedStatus()
    {
        var calls = Enumerable.Range(0, 60).Select(index =>
            new DurableToolCallOutcomeSummary(
                $"call-{index}", "tool", (DurableToolCallOutcomeStatus)(index % 3))).ToArray();

        var (summaries, truncated, counts) = DurableToolCallSummaryBudget.Create(calls);

        Assert.True(truncated);
        Assert.Equal(calls[^32..], summaries);
        Assert.Equal(new DurableToolCallOutcomeCounts(20, 20, 20), counts);
        Assert.Equal(new DurableToolCallOutcomeCounts(10, 9, 9),
            DurableToolCallSummaryBudget.GetOmittedCounts(counts, summaries));
    }

    [Fact]
    public void SummaryBudgetBoundsEscapedIdentitiesBySerializedBytes()
    {
        var calls = Enumerable.Range(0, 32).Select(index =>
            new DurableToolCallOutcomeSummary(
                $"{index}{new string('\0', 250)}",
                new string('\0', 256),
                DurableToolCallOutcomeStatus.Completed)).ToArray();

        var (summaries, truncated, counts) = DurableToolCallSummaryBudget.Create(calls);

        Assert.True(truncated);
        Assert.Equal(calls[^summaries.Count..], summaries);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(
            summaries, DurableAIJsonUtilities.DefaultOptions).Length <= DurableToolCallSummaryBudget.MaximumBytes);
        Assert.Equal(32 - summaries.Count,
            DurableToolCallSummaryBudget.GetOmittedCounts(counts, summaries).Completed);
    }

    [Fact]
    public void FailureMapperRestoresTypedFailureWithOriginalCauseAndOmittedCounts()
    {
        var details = new DurableTurnFailureDetails
        {
            RequestId = "failed-request",
            Message = "The model failed after a tool completed.",
            ErrorType = "ModelError",
            CommittedRevision = 3,
            ToolCallSummaries =
            [
                new("last-call", "write", DurableToolCallOutcomeStatus.OutcomeUncertain),
            ],
            ToolCallSummariesTruncated = true,
            ToolCallCounts = new(2, 1, 1),
        };
        var cause = new ApplicationFailureException(
            JsonSerializer.Serialize(details, DurableAIJsonUtilities.DefaultOptions),
            errorType: DurableTurnFailureMapper.TurnFailedErrorType,
            nonRetryable: true);

        var failure = Assert.IsType<DurableTurnFailedException>(
            DurableTurnFailureMapper.Map(cause, details.RequestId));

        Assert.Same(cause, failure.InnerException);
        Assert.Equal(3, failure.CommittedRevision);
        Assert.Equal("ModelError", failure.ErrorType);
        Assert.Equal(details.ToolCallSummaries, failure.ToolCallSummaries);
        Assert.Equal(new DurableToolCallOutcomeCounts(2, 1, 0), failure.OmittedToolCallCounts);
    }

    [Fact]
    public void MinimalOutcomeWithMaximumRequestIdFitsMinimumLedgerBudget()
    {
        var outcome = new DurableTerminalTurnOutcome
        {
            RequestId = new string('\0', 256),
            AdmissionFingerprint = new string('f', 64),
            OutcomeOmitted = true,
            IsMinimal = true,
            Status = "failed",
            CommittedRevision = long.MaxValue,
            ToolCallCounts = new DurableToolCallOutcomeCounts(
                int.MaxValue,
                int.MaxValue,
                int.MaxValue),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            outcome,
            DurableAIJsonUtilities.DefaultOptions);

        Assert.True(
            bytes.Length <= DurableTerminalTurnOutcomeLimits.MinimumBytes,
            $"Minimal outcome required {bytes.Length} bytes.");
    }

    [Fact]
    public void TerminalOutcomeBudgetFallsBackToMinimalWhenCompactCallSummariesDoNotFit()
    {
        var options = DurableAIJsonUtilities.DefaultOptions;
        var minimal = new DurableTerminalTurnOutcome
        {
            RequestId = "request-1",
            AdmissionFingerprint = new string('f', 64),
            OutcomeOmitted = true,
            IsMinimal = true,
            Status = "completed",
        };
        var full = minimal with
        {
            IsMinimal = false,
            Output = JsonSerializer.SerializeToElement(new string('x', 8192), options),
        };
        var compact = minimal with
        {
            IsMinimal = false,
            TurnInputFingerprint = new string('i', 64),
            ToolCallSummaries = Enumerable.Range(0, 32)
                .Select(index => new DurableToolCallOutcomeSummary(
                    $"{index}:{new string('c', 256)}",
                    new string('t', 256),
                    DurableToolCallOutcomeStatus.Completed))
                .ToArray(),
            ToolCallCounts = new DurableToolCallOutcomeCounts(32, 0, 0),
        };

        var fit = DurableTerminalTurnOutcomeBudget.Fit(
            full,
            compact,
            minimal,
            DurableTerminalTurnOutcomeLimits.MinimumBytes,
            options);

        Assert.True(fit.Outcome.IsMinimal);
        Assert.Null(fit.Outcome.TurnInputFingerprint);
        Assert.Null(fit.Outcome.ToolCallSummaries);
        Assert.Null(fit.Outcome.Output);
        Assert.True(fit.SerializedSizeBytes <= DurableTerminalTurnOutcomeLimits.MinimumBytes);
    }

    [Fact]
    public void FailureMapperCreatesTypedOutcomeTooLargeException()
    {
        var details = new DurableTurnOutcomeTooLargeDetails
        {
            RequestId = "request-1",
            Status = "completed",
            CommittedRevision = 7,
            IsMinimal = false,
            ToolCallSummaries =
            [
                new DurableToolCallOutcomeSummary(
                    "call-1",
                    "lookup",
                    DurableToolCallOutcomeStatus.Completed),
            ],
            ToolCallCounts = new DurableToolCallOutcomeCounts(1, 0, 0),
        };
        var message = JsonSerializer.Serialize(
            details,
            DurableAIJsonUtilities.DefaultOptions.GetTypeInfo(
                typeof(DurableTurnOutcomeTooLargeDetails)));
        var failure = new ApplicationFailureException(
            message,
            errorType: DurableTurnFailureMapper.OutcomeTooLargeErrorType,
            nonRetryable: true);

        var exception = Assert.IsType<DurableTurnOutcomeTooLargeException>(
            DurableTurnFailureMapper.Map(failure, "request-1"));

        Assert.Equal("request-1", exception.RequestId);
        Assert.Equal(7, exception.CommittedRevision);
        Assert.False(exception.IsMinimal);
        Assert.Equal(DurableToolCallOutcomeStatus.Completed, Assert.Single(exception.ToolCallSummaries).Status);
        Assert.Equal(1, exception.ToolCallCounts.Completed);
    }

    [Fact]
    public void FailureMapperCreatesRetryableBusyException()
    {
        var failure = new ApplicationFailureException(
            "Busy",
            errorType: "DurableTurnBusy",
            nonRetryable: true);

        var exception = Assert.IsType<DurableSessionBusyException>(
            DurableTurnFailureMapper.Map(failure, "request-2"));

        Assert.Equal("request-2", exception.RequestId);
        Assert.True(exception.IsRetryable);
    }
}
