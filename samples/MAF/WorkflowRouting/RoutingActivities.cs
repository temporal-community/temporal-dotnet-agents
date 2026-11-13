using System;
using System.Linq;
using Temporalio.Activities;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Microsoft.Agents.AI;

namespace WorkflowRouting;

/// <summary>
/// Activities that safely query the agent registry at runtime.
/// Activities are NOT replayed — their results are cached in workflow history.
/// This makes registry lookups safe even if the agent set changes between deployments.
/// </summary>
public class RoutingActivities(TemporalAgentsOptions options, IServiceProvider services)
{
    /// <summary>
    /// Returns descriptors for all registered agents that have a description.
    /// Agents without a description (e.g. the Classifier) are excluded automatically,
    /// so the LLM only sees routable specialist agents.
    /// </summary>
    /// <remarks>
    /// This is safe inside an activity because the result is recorded in Temporal's event
    /// history. On replay, the cached list is returned — the registry is never re-queried.
    /// Even if agents are added or removed between the original execution and a replay,
    /// the workflow sees the same agent list it saw originally.
    /// </remarks>
    [Activity("WorkflowRouting.GetAvailableAgents")]
    public AgentInfo[] GetAvailableAgents()
    {
        // Descriptions are registered alongside the agent via AddDurableAgent() — no
        // local dictionary needed. Agents without a description (e.g. the Classifier)
        // are excluded.
        return options.GetAgentDescriptors()
            .Select(d => new AgentInfo(d.Name, d.Description))
            .ToArray();
    }

    /// <summary>
    /// Matches a model-selected name to a described durable agent and returns its registered
    /// spelling. Uses the caller's registered fallback when there is no match.
    /// </summary>
    [Activity("WorkflowRouting.ValidateAgent")]
    public string ValidateAgent(string agentName, string fallback)
    {
        var registeredName = options.GetAgentDescriptors()
            .Select(descriptor => descriptor.Name)
            .FirstOrDefault(name => string.Equals(
                name, agentName, StringComparison.OrdinalIgnoreCase));
        return registeredName ?? fallback;
    }

    [Activity("WorkflowRouting.RunAgent")]
    public async Task<string> RunAgentAsync(string agentName, string sessionWorkflowId, string requestId, string message)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        AIAgent agent = services.GetTemporalAgentProxy(agentName);
        var response = await agent.RunAsync(
            message,
            new TemporalAgentSession(sessionId),
            new TemporalAgentRunOptions { RequestId = requestId }).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }
}

/// <summary>
/// Serializable record for passing agent metadata through Temporal activity results.
/// </summary>
public record AgentInfo(string Name, string Description);
