using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tools;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Specifies a tool and its per-tool options for registration via
/// <see cref="DurableAgentBuilder.AddContextProvider(AIContextProvider, IEnumerable{DurableToolRegistrationSpec}?)"/>.
/// </summary>
/// <remarks>
/// Tool activities use one attempt by default. The agent-level retry policy is the only way to
/// opt tools into retries and applies to every tool on that agent.
/// </remarks>
public sealed record DurableToolRegistrationSpec(
    AIFunction Tool,
    Action<DurableToolOptions>? Configure = null);
