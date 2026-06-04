namespace Temporalio.Extensions.AI;

/// <summary>
/// Base context supplied to <see cref="IDurableToolInterceptor{TContext}.BeforeToolCallAsync"/>.
/// Describes the tool call that the dispatch loop is about to execute as a Temporal activity.
/// </summary>
/// <remarks>
/// This type is not sealed. <c>AgentToolContext</c> (in <c>Temporalio.Extensions.Agents</c>)
/// extends it with MAF-specific fields (<c>AgentName</c> and <c>StateBag</c>). Custom
/// implementations can also subclass to carry additional application-specific context.
/// </remarks>
public class DurableToolContext
{
    /// <summary>Gets the name of the tool being invoked.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Gets the arguments the LLM supplied for this tool call.
    /// Keys and values mirror the LLM's <c>FunctionCallContent.Arguments</c> dictionary.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }

    /// <summary>
    /// Gets the LLM-assigned call identifier, used to correlate this call with its result
    /// in the chat message history. May be <see langword="null"/> for models that do not
    /// emit call IDs.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Gets the session identifier for the running workflow, if available.
    /// For <c>AgentWorkflow</c>-backed sessions this is the Temporal workflow ID.
    /// May differ from the logical session ID when the interceptor runs inside a
    /// sub-agent or scheduled-job workflow.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the conversation identifier, if available. Populated on the MEAI path (set by
    /// <c>DurableChatActivities.RunToolInterceptorAsync</c>); <see langword="null"/> on the
    /// MAF path (where <c>AgentToolContext</c> in <c>Temporalio.Extensions.Agents</c> provides
    /// MAF-specific fields instead).
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Gets the correlation identifier, if available. Populated on the MEAI path (set by
    /// <c>DurableChatActivities.RunToolInterceptorAsync</c>); <see langword="null"/> on the
    /// MAF path (where <c>AgentToolContext</c> in <c>Temporalio.Extensions.Agents</c> provides
    /// MAF-specific fields instead).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Gets the turn number within the current session, if available. Populated on the MEAI
    /// path (set by <c>DurableChatActivities.RunToolInterceptorAsync</c>);
    /// <see langword="null"/> on the MAF path (where <c>AgentToolContext</c> in
    /// <c>Temporalio.Extensions.Agents</c> provides MAF-specific fields instead).
    /// </summary>
    public int? TurnNumber { get; init; }

    /// <summary>
    /// Gets optional key/value metadata carried for audit or routing purposes.
    /// <see langword="null"/> when not set by the caller.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
