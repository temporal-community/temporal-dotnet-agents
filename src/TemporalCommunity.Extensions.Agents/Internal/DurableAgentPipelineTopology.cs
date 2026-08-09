using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>Validates structural invariants of a configured MAF agent pipeline.</summary>
internal static class DurableAgentPipelineTopology
{
    /// <summary>
    /// Requires the exact library-created inner agent to remain reachable through inspectable
    /// <see cref="DelegatingAIAgent"/> links.
    /// </summary>
    internal static void EnsurePreservesInnerAgent(
        string agentName,
        AIAgent builtAgent,
        AIAgent expectedInnerAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(builtAgent);
        ArgumentNullException.ThrowIfNull(expectedInnerAgent);

        if (AgentChainWalker.ContainsReference(builtAgent, expectedInnerAgent))
        {
            return;
        }

        throw new DurableConfigurationException(
            $"Agent '{agentName}' has a ConfigureAgentPipeline factory that removed or hid " +
            "the library-created inner agent. Every custom wrapper must derive from " +
            "DelegatingAIAgent and pass the factory's supplied inner agent to its base " +
            "constructor. Returning an unrelated agent or an opaque AIAgent wrapper is not " +
            "supported because the durable leaf cannot be verified.");
    }
}
