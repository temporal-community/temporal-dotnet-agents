using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>Deterministic utilities for use inside Temporal workflows.</summary>
public static class WorkflowAgents
{
    /// <summary>
    /// Generates a deterministic <see cref="TemporalAgentSessionId"/> using
    /// <see cref="Workflow.NewGuid"/>.
    /// </summary>
    /// <remarks>
    /// Use the returned ID when an activity invokes a registered
    /// <see cref="TemporalAIAgentProxy"/>. The same ID is produced on replay and can be
    /// persisted with the parent workflow's input or state.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when called outside a Temporal workflow.</exception>
    public static TemporalAgentSessionId NewAgentSessionId(string agentName)
    {
        if (!Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                $"{nameof(NewAgentSessionId)} can only be called from within a Temporal workflow. " +
                "Use TemporalAgentSessionId.WithRandomKey(agentName) in external code.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        return TemporalAgentSessionId.WithDeterministicKey(agentName, Workflow.NewGuid());
    }
}
