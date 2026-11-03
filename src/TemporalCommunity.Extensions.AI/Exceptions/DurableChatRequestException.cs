namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Reports a failed durable chat submission together with the logical request ID needed to
/// retry or inspect that submission.
/// </summary>
public sealed class DurableChatRequestException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatRequestException"/> class.
    /// </summary>
    public DurableChatRequestException(string requestId, Exception innerException)
        : base($"Durable chat request '{requestId}' did not return a response.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(innerException);
        RequestId = requestId;
    }

    /// <summary>The logical request ID to reuse only with the same effect-bearing input.</summary>
    public string RequestId { get; }
}
