namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Base type for configuration / wiring failures surfaced by the durable AI
/// libraries (<c>TemporalCommunity.Extensions.AI</c> and <c>TemporalCommunity.Extensions.Agents</c>).
/// </summary>
/// <remarks>
/// <para>
/// Specific subtypes encode distinct failure modes — e.g.,
/// <see cref="DurableReplayCompatibilityException"/> for rolling-deploy
/// polymorphism skew. Callers that wish to handle any durable-configuration
/// problem uniformly can catch this base type; callers that need to react to a
/// specific failure mode catch the relevant subtype.
/// </para>
/// <para>
/// This type is intentionally stable (no <c>[Experimental]</c> attribute) so
/// that user-facing <c>catch</c> blocks remain valid across preview-to-stable
/// transitions of the durable libraries.
/// </para>
/// </remarks>
public class DurableConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableConfigurationException"/> class.
    /// </summary>
    public DurableConfigurationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableConfigurationException"/> class with a specified error
    /// message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public DurableConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableConfigurationException"/> class with a specified error
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">
    /// The exception that is the cause of the current exception, or
    /// <see langword="null"/> if none.
    /// </param>
    public DurableConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
