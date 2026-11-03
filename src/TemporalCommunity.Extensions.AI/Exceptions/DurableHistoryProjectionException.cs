namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Indicates that a retained-history reducer returned an invalid projection.
/// </summary>
public sealed class DurableHistoryProjectionException : DurableConfigurationException
{
    /// <summary>Initializes a new instance of the exception with a safe reason.</summary>
    public DurableHistoryProjectionException(string message)
        : base(message)
    {
    }
}
