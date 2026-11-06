namespace TemporalCommunity.Extensions.Agents.Exceptions;

/// <summary>
/// Marks a tool failure that is safe to return to the model because the tool guarantees that
/// it failed before performing any external side effect.
/// </summary>
/// <remarks>
/// The message supplied here is deliberately model-visible. Do not include secrets, raw
/// exception details, or data the model is not allowed to see.
/// </remarks>
public sealed class DurableToolFailureException : Exception
{
    private const int MaximumModelVisibleMessageBytes = 1024;

    /// <summary>
    /// Initializes a failure whose safe, sanitized message may be returned to the model.
    /// </summary>
    /// <param name="modelVisibleMessage">A bounded, sanitized explanation suitable for model input.</param>
    /// <param name="innerException">The underlying cause, retained for worker-side diagnostics.</param>
    public DurableToolFailureException(string modelVisibleMessage, Exception? innerException = null)
        : base(ValidateMessage(modelVisibleMessage), innerException)
    {
        ModelVisibleMessage = modelVisibleMessage;
    }

    /// <summary>Gets the sanitized message that may be returned to the model.</summary>
    public string ModelVisibleMessage { get; }

    private static string ValidateMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (System.Text.Encoding.UTF8.GetByteCount(message) > MaximumModelVisibleMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"The model-visible tool failure message cannot exceed {MaximumModelVisibleMessageBytes} UTF-8 bytes.");
        }

        return message;
    }
}
