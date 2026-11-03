namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when a durable AI payload exceeds its configured conservative transport-size budget.
/// </summary>
public sealed class DurableTransportPayloadTooLargeException : InvalidOperationException
{
    internal DurableTransportPayloadTooLargeException(
        string boundary,
        int serializedSizeBytes,
        int maximumPayloadSizeBytes,
        int payloadCodecExpansionAllowanceBytes)
        : base(
            $"The serialized payload for {boundary} is {serializedSizeBytes} bytes, exceeding " +
            $"the configured transport budget of {maximumPayloadSizeBytes} bytes after reserving " +
            $"{payloadCodecExpansionAllowanceBytes} bytes for payload-codec expansion. " +
            "Reduce the payload or increase the configured budget only when it remains below " +
            "the Temporal service limit.")
    {
        Boundary = boundary;
        SerializedSizeBytes = serializedSizeBytes;
        MaximumPayloadSizeBytes = maximumPayloadSizeBytes;
        PayloadCodecExpansionAllowanceBytes = payloadCodecExpansionAllowanceBytes;
    }

    /// <summary>Gets the transport boundary where the payload was rejected.</summary>
    public string Boundary { get; }

    /// <summary>Gets the complete serialized Temporal payload size, including metadata.</summary>
    public int SerializedSizeBytes { get; }

    /// <summary>Gets the configured maximum payload budget.</summary>
    public int MaximumPayloadSizeBytes { get; }

    /// <summary>Gets the size reserved for payload-codec expansion.</summary>
    public int PayloadCodecExpansionAllowanceBytes { get; }
}
