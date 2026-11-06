using System.Text.Json;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class DurableTerminalOutcomeTests
{
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
