namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when a chat pipeline being configured for durable execution already contains
/// function-invocation middleware, such as <c>FunctionInvokingChatClient</c> from
/// Microsoft.Extensions.AI.
/// </summary>
/// <remarks>
/// <para>
/// The library invokes tools as separate <c>TemporalCommunity.Extensions.AI.InvokeFunction</c>
/// activities. If a user composes the chat pipeline with <c>UseFunctionInvocation()</c> before
/// configuring durable execution, both layers would attempt to handle tool calls—yielding
/// double-execution, lost durability guarantees, or silently skipped activities depending on
/// layering order. Detecting the conflict up front forces the user to remove the conflicting
/// middleware.
/// </para>
/// <para>
/// This type is intentionally stable (no <c>[Experimental]</c> attribute) — once a release
/// surfaces this exception, callers should be able to <c>catch</c> it without their code
/// breaking on a preview-to-stable transition. The base type
/// <see cref="DurableConfigurationException"/> is the broad-catch entry point for any durable
/// wiring failure.
/// </para>
/// </remarks>
public sealed class DurableFunctionInvocationConflictException : DurableConfigurationException
{
    /// <summary>
    /// Gets the fully-qualified type name of the offending middleware that was detected in the
    /// user's chat pipeline, such as
    /// <c>Microsoft.Extensions.AI.FunctionInvokingChatClient</c>.
    /// </summary>
    public required string OffendingType { get; init; }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableFunctionInvocationConflictException"/> class with a specified error
    /// message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public DurableFunctionInvocationConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DurableFunctionInvocationConflictException"/> class with a specified error
    /// message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">
    /// The exception that is the cause of the current exception, or <see langword="null"/> if
    /// none.
    /// </param>
    public DurableFunctionInvocationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
