namespace Temporalio.Extensions.AI.Exceptions;

/// <summary>
/// Thrown when the MEAI library detects that a user has wired both
/// <c>.UseFunctionInvocation()</c> on their <see cref="Microsoft.Extensions.AI.IChatClient"/>
/// chain AND <c>.AsDurable()</c>-wrapped function tools — the two patterns are mutually
/// exclusive and silently produce in-process tool execution (durability violated) when mixed.
/// </summary>
/// <remarks>
/// <para>
/// Detected by the A-check (worker startup) or B-check (first-invocation backstop) per
/// artifacts/maf-feature-gap-analysis.md → "MEAI mixed-pattern enforcement". The two valid
/// patterns are:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Pattern 1 (idiomatic, in-process tool loop):</b>
///       <c>.UseFunctionInvocation()</c> on the chat client + tools as plain
///       <see cref="Microsoft.Extensions.AI.AIFunction"/>s passed via <c>ChatOptions.Tools</c>.
///       Tools execute in-process inside the chat activity; the LLM's function-call loop is
///       handled by MEAI's <c>FunctionInvokingChatClient</c>. The default
///       <c>DurableChatWorkflow</c> supports this pattern.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Pattern 2 (per-tool durable activities):</b>
///       Tools wrapped with <c>.AsDurable()</c> + the user writes a custom workflow that
///       explicitly dispatches each tool call as a separate Temporal activity via
///       <c>DurableAIFunction.InvokeAsync</c>. <see cref="Microsoft.Extensions.AI.IChatClient"/>
///       MUST NOT include <c>.UseFunctionInvocation()</c>, or its in-process loop would
///       short-circuit the durable dispatch.
///     </description>
///   </item>
/// </list>
/// <para>
/// Mixing the two — durable-tool registration AND
/// <c>FunctionInvokingChatClient</c> in the chain — is silently broken: the chat client's
/// in-process function-invocation middleware intercepts tool calls before they ever reach
/// the durable dispatch path, so durability is violated without any error. The enforcement
/// in Step 4 detects the combination and throws this exception at the earliest opportunity.
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
    /// the canonical message describing the two-pattern conflict.
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
        "Mixed-pattern conflict: the MEAI library detected that you have both " +
        "registered durable function tools (via AddDurableTools / .AsDurable()) AND " +
        "configured your IChatClient with .UseFunctionInvocation(). These two patterns are " +
        "mutually exclusive: FunctionInvokingChatClient intercepts tool calls in-process " +
        "before they reach the durable dispatch path, so per-tool durability is silently " +
        "violated when both are present. Pick exactly one: (a) Pattern 1 — remove " +
        ".AsDurable() and let the chat client handle tools in-process (no per-tool " +
        "durability); or (b) Pattern 2 — remove .UseFunctionInvocation() from the chat " +
        "client and let the durable workflow dispatch each tool as a separate activity.";
}
