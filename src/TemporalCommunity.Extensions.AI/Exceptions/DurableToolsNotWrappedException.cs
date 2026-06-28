namespace TemporalCommunity.Extensions.AI.Exceptions;

/// <summary>
/// Thrown at activity time by <see cref="DurableChatActivities.GetResponseAsync"/> when the
/// LLM returns tool-call content but the chat client pipeline has no handler configured to
/// dispatch them — i.e. the user registered tools via
/// <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction[])"/>
/// but neither <c>UseFunctionInvocation()</c> is in the chat-client chain (Pattern 1) nor is
/// the call coming through <see cref="DurableChatSessionClient"/> (Pattern 3) nor are tools
/// wrapped with <c>.AsDurable()</c> inside a custom workflow (Pattern 2).
/// </summary>
/// <remarks>
/// <para>
/// This exception exists to surface a silent-failure footgun: without it, the workflow would
/// receive a <see cref="global::Microsoft.Extensions.AI.ChatResponse"/> with unresolved
/// <see cref="global::Microsoft.Extensions.AI.FunctionCallContent"/> entries, return it to
/// the caller, and the tool would never execute. The exception fires only when tool calls
/// are actually returned and there's no path to dispatch them.
/// </para>
/// <para>
/// Fix options surfaced in the exception message:
/// </para>
/// <list type="number">
///   <item>Use <see cref="DurableChatSessionClient"/> instead of <c>DurableChatClient</c> middleware.</item>
///   <item>Wrap tools with <c>.AsDurable()</c> in your custom workflow code (Pattern 2).</item>
///   <item>Use <c>UseFunctionInvocation()</c> in the chat-client chain (Pattern 1).</item>
/// </list>
/// </remarks>
public sealed class DurableToolsNotWrappedException : DurableConfigurationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableToolsNotWrappedException"/> class
    /// with a default message.
    /// </summary>
    public DurableToolsNotWrappedException()
        : base(DefaultMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableToolsNotWrappedException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public DurableToolsNotWrappedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableToolsNotWrappedException"/> class
    /// with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public DurableToolsNotWrappedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal const string DefaultMessage =
        "LLM returned tool calls but no dispatch handler is configured. " +
        "Either (1) use DurableChatSessionClient instead of DurableChatClient middleware, " +
        "(2) wrap tools with .AsDurable() in your custom workflow code (Pattern 2), " +
        "or (3) use UseFunctionInvocation() in the chat client chain (Pattern 1).";
}
