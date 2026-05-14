namespace Temporalio.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when a chat pipeline that the durable agent libraries are wiring up already
/// contains a function-invocation middleware (for example,
/// <c>FunctionInvocationDelegatingAgent</c> from Microsoft.Agents.AI or
/// <c>FunctionInvokingChatClient</c> from Microsoft.Extensions.AI).
/// </summary>
/// <remarks>
/// <para>
/// The durable libraries are responsible for invoking tools as separate Temporal activities
/// (<c>InvokeAgentTool</c> for <c>Temporalio.Extensions.Agents</c>, <c>InvokeFunction</c> for
/// <c>Temporalio.Extensions.AI</c>). If a user composes their pipeline with
/// <c>UseFunctionInvocation()</c> and then hands that pipeline to <c>AddDurableAgent</c>, both
/// layers would attempt to handle tool calls — yielding double-execution, lost durability
/// guarantees, or silent skipped activities depending on layering order. Detecting the conflict
/// up front and throwing this exception forces the user to remove the redundant middleware.
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
    /// user's chat pipeline (e.g. <c>Microsoft.Agents.AI.FunctionInvocationDelegatingAgent</c> or
    /// <c>Microsoft.Extensions.AI.FunctionInvokingChatClient</c>).
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
