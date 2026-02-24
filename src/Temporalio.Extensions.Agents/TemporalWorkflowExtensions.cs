// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods for use inside Temporal workflows.
/// Equivalent to <c>TaskOrchestrationContextExtensions</c>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TemporalWorkflowExtensions
{
    /// <summary>
    /// Gets a <see cref="TemporalAIAgent"/> for use in an orchestrating workflow.
    /// </summary>
    /// <param name="agentName">The registered agent name.</param>
    /// <param name="activityOptions">Optional activity options. Defaults to 30-minute StartToCloseTimeout.</param>
    public static TemporalAIAgent GetAgent(string agentName, ActivityOptions? activityOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return new TemporalAIAgent(agentName, activityOptions);
    }

    /// <summary>
    /// Generates a deterministic <see cref="TemporalAgentSessionId"/> using
    /// <see cref="Workflow.NewGuid()"/>.
    /// </summary>
    public static TemporalAgentSessionId NewAgentSessionId(string agentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return TemporalAgentSessionId.WithDeterministicKey(agentName, Workflow.NewGuid());
    }
}
