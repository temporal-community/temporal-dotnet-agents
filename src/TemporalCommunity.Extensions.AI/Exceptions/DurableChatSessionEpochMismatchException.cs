namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when a durable chat request expected a different session lineage.
/// </summary>
public sealed class DurableChatSessionEpochMismatchException : Exception
{
    internal const string ErrorType = "DurableSessionEpochMismatch";

    internal DurableChatSessionEpochMismatchException(string expectedSessionEpoch, Exception innerException)
        : base(
            $"The active durable session epoch does not match the expected epoch '{expectedSessionEpoch}'. " +
            "Read the current epoch and retry only if starting a new session lineage is intended.",
            innerException)
    {
        ExpectedSessionEpoch = expectedSessionEpoch;
    }

    /// <summary>The epoch the caller expected.</summary>
    public string ExpectedSessionEpoch { get; }
}
