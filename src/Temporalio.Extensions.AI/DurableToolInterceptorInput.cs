namespace Temporalio.Extensions.AI;

/// <summary>
/// Input for the <c>RunToolInterceptor</c> activity on the MEAI path.
/// Carries enough context for the interceptor to make a pre-tool decision.
/// </summary>
internal sealed class DurableToolInterceptorInput
{
    /// <summary>Name of the tool being intercepted.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Arguments the LLM supplied for this tool call. May be <see langword="null"/> when the
    /// LLM did not emit any arguments (parameterless tool calls).
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    /// <summary>LLM-assigned call ID. May be <see langword="null"/>.</summary>
    public string? CallId { get; init; }

    /// <summary>Conversation identifier supplied by the session client.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Per-turn correlation identifier matching the request entry.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Turn number within the current session.</summary>
    public int? TurnNumber { get; init; }
}
