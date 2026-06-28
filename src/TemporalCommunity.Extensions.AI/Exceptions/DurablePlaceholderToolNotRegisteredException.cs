namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown at activity time by <c>DurableChatActivities.SwapPlaceholderTools</c> when a
/// <c>ToolNamePlaceholder</c> left over from wire deserialization cannot be resolved to a
/// real <see cref="global::Microsoft.Extensions.AI.AIFunction"/> in the
/// <c>DurableFunctionRegistry</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="global::Microsoft.Extensions.AI.ChatOptions.Tools"/> serializes as a list of
/// tool <em>names</em> only (see <c>ChatOptionsToolsJsonConverter</c>). On the activity side,
/// each name is swapped back to its registered <c>AIFunction</c>. If a name has no registration,
/// the previous behavior was to log a warning and silently drop the tool — the LLM would then be
/// instructed to use a tool it never actually received, producing confusing, hard-to-diagnose
/// behavior at runtime.
/// </para>
/// <para>
/// This is a configuration error, not a transient failure: retrying the activity produces the
/// same result every time. It is surfaced as a non-retryable
/// <see cref="global::Temporalio.Exceptions.ApplicationFailureException"/> whose
/// <c>ErrorType</c> is <c>nameof(DurablePlaceholderToolNotRegisteredException)</c>, so workflow
/// callers can still match on the typed name.
/// </para>
/// <para>
/// Fix: register the tool so it is present in the registry — via
/// <c>AddDurableTools(...)</c> on the worker builder, or by wrapping the function with
/// <c>.AsDurable()</c>.
/// </para>
/// </remarks>
public sealed class DurablePlaceholderToolNotRegisteredException : DurableConfigurationException
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurablePlaceholderToolNotRegisteredException"/> class with a default message.
    /// </summary>
    public DurablePlaceholderToolNotRegisteredException()
        : base(BuildMessage("(unknown)"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurablePlaceholderToolNotRegisteredException"/> class with a specified error
    /// message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public DurablePlaceholderToolNotRegisteredException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurablePlaceholderToolNotRegisteredException"/> class with a specified error
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public DurablePlaceholderToolNotRegisteredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Builds the canonical error message naming the unresolved tool and the fix.
    /// </summary>
    /// <param name="toolName">The name of the placeholder tool that could not be resolved.</param>
    /// <returns>A descriptive, actionable error message.</returns>
    internal static string BuildMessage(string toolName) =>
        $"Tool '{toolName}' was referenced in ChatOptions.Tools but is not registered in the " +
        "DurableFunctionRegistry, so it cannot be dispatched. Register it via " +
        "AddDurableTools(...) on the worker builder, or wrap the function with .AsDurable(), " +
        "before the chat request is made.";
}
