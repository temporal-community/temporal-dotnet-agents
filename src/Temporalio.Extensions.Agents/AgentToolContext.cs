using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Context supplied to <see cref="IAgentToolInterceptor.BeforeToolCallAsync"/>. Describes the
/// tool call that the turn loop is about to dispatch as a Temporal activity.
/// </summary>
public sealed class AgentToolContext
{
    /// <summary>Gets the name of the agent that owns this tool call.</summary>
    public required string AgentName { get; init; }

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
    public required string? CallId { get; init; }

    /// <summary>
    /// Gets a read-only snapshot of the agent's session state at turn start.
    /// Deserialized from <c>_currentStateBag</c> inside the interceptor activity using the
    /// same pattern as <c>TemporalAgentSession.FromStateBag</c>.
    /// Mutations made to this object are NOT persisted back — only the LLM-step activity's
    /// <c>UpdatedStateBag</c> flows back to the workflow.
    /// May be <see langword="null"/> when no state has been accumulated yet.
    /// </summary>
    public AgentSessionStateBag? StateBag { get; init; }
}
