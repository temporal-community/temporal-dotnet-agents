namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when the chat-client pipeline used by a durable session includes
/// <c>.UseFunctionInvocation()</c> while durable tools are registered.
/// </summary>
/// <remarks>
/// <para>
/// Detected by the startup validation or the first-invocation backstop. A managed durable session
/// has one function-call loop:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Register tools through <c>AddDurableTools()</c>. The workflow passes their schemas to the
///       model and dispatches each returned call as a Temporal activity.
///     </description>
///   </item>
///   <item>
///     <description>
///       Do not add <c>.UseFunctionInvocation()</c> to that session's
///       <see cref="Microsoft.Extensions.AI.IChatClient"/> pipeline.
///     </description>
///   </item>
/// </list>
/// <para>
/// Inline middleware would intercept function calls before the workflow can schedule tool
/// activities, so the validator rejects the configuration before it can silently violate the
/// durable-session contract.
/// </para>
/// <para>
/// Stable subtype (no <c>[Experimental]</c> attribute) — once an SDK release ships this
/// enforcement, the exception type is part of the public catch surface.
/// </para>
/// </remarks>
public sealed class DurableMixedPatternException : DurableConfigurationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableMixedPatternException"/> class with
    /// the canonical configuration-error message.
    /// </summary>
    public DurableMixedPatternException()
        : base(BuildMessage())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableMixedPatternException"/> class with
    /// a specified error message.
    /// </summary>
    public DurableMixedPatternException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableMixedPatternException"/> class with
    /// a specified error message and inner exception.
    /// </summary>
    public DurableMixedPatternException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private static string BuildMessage() =>
        "Durable chat sessions cannot use .UseFunctionInvocation() when tools are registered " +
        "with AddDurableTools(). The workflow owns the model/tool loop and schedules each tool " +
        "as a Temporal activity. Remove .UseFunctionInvocation() from the chat-client pipeline.";
}
