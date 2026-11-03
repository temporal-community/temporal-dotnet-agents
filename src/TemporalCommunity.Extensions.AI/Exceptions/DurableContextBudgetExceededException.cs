namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Indicates that the protected model request cannot fit within the configured context budget.
/// </summary>
public sealed class DurableContextBudgetExceededException : DurableConfigurationException
{
    /// <summary>Initializes a new instance of the exception.</summary>
    public DurableContextBudgetExceededException(int estimatedInputTokens, int outputTokenReserve, int maximumContextTokens)
        : base("The protected model request exceeds the configured context-token budget.")
    {
        EstimatedInputTokens = estimatedInputTokens;
        OutputTokenReserve = outputTokenReserve;
        MaximumContextTokens = maximumContextTokens;
    }

    /// <summary>The estimator's input-token estimate.</summary>
    public int EstimatedInputTokens { get; }

    /// <summary>The configured output-token reserve.</summary>
    public int OutputTokenReserve { get; }

    /// <summary>The configured total model-context limit.</summary>
    public int MaximumContextTokens { get; }
}
