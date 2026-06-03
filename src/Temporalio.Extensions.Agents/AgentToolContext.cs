using Microsoft.Agents.AI;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Context supplied to <see cref="IAgentToolInterceptor.BeforeToolCallAsync"/>. Extends
/// <see cref="DurableToolContext"/> with MAF-specific fields for agent sessions.
/// </summary>
/// <remarks>
/// The base class (<see cref="DurableToolContext"/>) provides the cross-library fields:
/// <c>ToolName</c>, <c>Arguments</c>, <c>CallId</c>, <c>SessionId</c>, and additional
/// optional context fields. This class adds <c>AgentName</c> and <c>StateBag</c>, which are
/// specific to Microsoft Agent Framework sessions.
/// </remarks>
public sealed class AgentToolContext : DurableToolContext
{
    /// <summary>Gets the name of the agent that owns this tool call.</summary>
    public required string AgentName { get; init; }

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
