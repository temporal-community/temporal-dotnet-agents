using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Indicates that a durable model response did not contain a valid structured result.
/// </summary>
public sealed class DurableStructuredOutputException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableStructuredOutputException"/> class.
    /// </summary>
    /// <param name="response">The original durable response and its provider metadata.</param>
    /// <param name="failureReason">The stable structured-output failure category.</param>
    public DurableStructuredOutputException(
        DurableSessionResponse response,
        DurableStructuredOutputFailureReason failureReason)
        : base(failureReason switch
        {
            DurableStructuredOutputFailureReason.IncompleteResponse =>
                "The durable chat turn did not complete with a final structured response.",
            DurableStructuredOutputFailureReason.InvalidResponse =>
                "The durable chat response did not contain a valid non-null JSON value for the requested type.",
            _ => throw new ArgumentOutOfRangeException(nameof(failureReason)),
        })
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
        FailureReason = failureReason;
    }

    /// <summary>The original response, including its retained messages and metadata.</summary>
    public DurableSessionResponse Response { get; }

    /// <summary>The stable category describing why the typed result was unavailable.</summary>
    public DurableStructuredOutputFailureReason FailureReason { get; }
}

/// <summary>Stable failure categories for typed durable chat results.</summary>
public enum DurableStructuredOutputFailureReason
{
    /// <summary>The managed turn returned an incomplete or non-final response.</summary>
    IncompleteResponse,

    /// <summary>The final response could not be deserialized as a non-null requested value.</summary>
    InvalidResponse,
}
