using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI;

/// <summary>Describes the result of requesting cancellation of a durable turn.</summary>
[JsonConverter(typeof(JsonNumberEnumConverter<DurableTurnCancellationStatus>))]
public enum DurableTurnCancellationStatus
{
    /// <summary>The workflow requested cancellation of the matching active turn.</summary>
    CancellationRequested = 0,

    /// <summary>The matching turn completed before the cancellation update was handled.</summary>
    AlreadyCompleted = 1,
}

/// <summary>Typed outcome of a workflow-owned durable turn cancellation request.</summary>
public sealed record DurableTurnCancellationResult
{
    /// <summary>The caller-known request ID that was targeted.</summary>
    public required string RequestId { get; init; }

    /// <summary>The race-aware cancellation outcome.</summary>
    public required DurableTurnCancellationStatus Status { get; init; }
}
