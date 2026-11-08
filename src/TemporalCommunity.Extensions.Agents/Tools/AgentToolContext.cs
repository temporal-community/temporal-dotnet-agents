using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.Agents.Tools;

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
    /// Gets the agent's session state snapshot at turn start. Deserialized from
    /// <c>_currentStateBag</c> inside the interceptor activity using the same pattern as
    /// <c>TemporalAgentSession.FromStateBag</c>.
    /// May be <see langword="null"/> when no state has been accumulated yet.
    /// </summary>
    /// <remarks>
    /// Mutations the interceptor makes to this bag during <c>BeforeToolCallAsync</c> ARE
    /// persisted back (X-2): the activity serializes the changed bag into
    /// <c>DurableToolInterceptorResult.UpdatedStateBag</c>, which the workflow converts to a
    /// key-level delta and applies before tool dispatch. When the bag is <see langword="null"/> (no state
    /// yet) there is nothing to mutate; an interceptor that needs to seed fresh state should do
    /// so via the LLM-step path. Concurrent interceptors in one turn are merged deterministically
    /// in tool-call index order (later index wins on key conflict).
    /// </remarks>
    public AgentSessionStateBag? StateBag { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the tool was registered with
    /// <see cref="DurableToolOptions.RequireApproval()"/>. The workflow enforces per-call
    /// approval independently of the interceptor's decision.
    /// </summary>
    public bool RequiresApproval { get; init; }

}
