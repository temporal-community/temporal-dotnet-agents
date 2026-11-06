using System.Text.Json;
using TemporalCommunity.Extensions.AI.Internal;

namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Indicates that a different request was rejected because the session already has an active
/// turn. The request may be retried with backoff; the workflow does not queue it.
/// </summary>
public sealed class DurableSessionBusyException : InvalidOperationException
{
    internal DurableSessionBusyException(string? requestId, Exception innerException)
        : base("The durable session is busy. Retry the request with backoff.", innerException) =>
        this.RequestId = requestId;

    /// <summary>The request ID that was rejected.</summary>
    public string? RequestId { get; }

    /// <summary>Whether the caller may retry after backoff.</summary>
    public bool IsRetryable => true;
}

/// <summary>
/// Indicates that a retained terminal turn outcome was too large to replay in full. The
/// workflow retains a compact or minimal record, so retrying the same request ID does not
/// re-dispatch the turn.
/// </summary>
public sealed class DurableTurnOutcomeTooLargeException : InvalidOperationException
{
    internal DurableTurnOutcomeTooLargeException(
        DurableTurnOutcomeTooLargeDetails details,
        Exception innerException)
        : base(
            $"The terminal outcome for request '{details.RequestId}' was too large to replay. " +
            "The turn was not re-executed.",
            innerException)
    {
        this.RequestId = details.RequestId;
        this.Status = details.Status;
        this.IsMinimal = details.IsMinimal;
        this.CommittedRevision = details.CommittedRevision;
        this.ErrorType = details.ErrorType;
        this.ToolCallSummaries = details.ToolCallSummaries ?? [];
        this.ToolCallSummariesTruncated = details.ToolCallSummariesTruncated;
        this.ToolCallCounts = details.ToolCallCounts
            ?? new DurableToolCallOutcomeCounts(0, 0, 0);
    }

    /// <summary>The logical request ID whose outcome could not be replayed.</summary>
    public string RequestId { get; }

    /// <summary>Whether the original turn completed or failed.</summary>
    public string Status { get; }

    /// <summary>Whether only request identity and per-status counts fit in the ledger.</summary>
    public bool IsMinimal { get; }

    /// <summary>The committed session-state revision, when the workflow exposes one.</summary>
    public long? CommittedRevision { get; }

    /// <summary>The original error type when a compact failure record could retain it.</summary>
    public string? ErrorType { get; }

    /// <summary>Bounded per-call details; empty when the minimal record was used.</summary>
    public IReadOnlyList<DurableToolCallOutcomeSummary> ToolCallSummaries { get; }

    /// <summary>Whether additional calls were omitted from the bounded summary list.</summary>
    public bool ToolCallSummariesTruncated { get; }

    /// <summary>Counts for all calls in the original turn.</summary>
    public DurableToolCallOutcomeCounts ToolCallCounts { get; }

    internal static DurableTurnOutcomeTooLargeException FromFailure(
        string message,
        Exception innerException)
    {
        var details = JsonSerializer.Deserialize(
            message,
            DurableAIJsonUtilities.DefaultOptions.GetTypeInfo(
                typeof(DurableTurnOutcomeTooLargeDetails))) as DurableTurnOutcomeTooLargeDetails
            ?? throw new JsonException("The terminal-outcome failure details were missing.");
        return new DurableTurnOutcomeTooLargeException(details, innerException);
    }
}

/// <summary>The outcome status recorded for one tool call in a compact terminal outcome.</summary>
public enum DurableToolCallOutcomeStatus
{
    /// <summary>The tool activity returned a result.</summary>
    Completed,

    /// <summary>The call was not dispatched or completed with a known failure.</summary>
    Failed,

    /// <summary>The activity was dispatched, but its result was not observed.</summary>
    OutcomeUncertain,
}

/// <summary>A bounded, result-free summary of one tool call.</summary>
public sealed record DurableToolCallOutcomeSummary(
    string? CallId,
    string FunctionName,
    DurableToolCallOutcomeStatus Status);

/// <summary>Per-status call counts retained when even the compact call list does not fit.</summary>
public sealed record DurableToolCallOutcomeCounts(
    int Completed,
    int Failed,
    int OutcomeUncertain);
