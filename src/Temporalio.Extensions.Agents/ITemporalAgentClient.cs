// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Client for running agents via Temporal workflow updates.
/// </summary>
public interface ITemporalAgentClient
{
    /// <summary>
    /// Runs an agent by sending a Temporal workflow update and waiting for the response.
    /// Starts the workflow if it is not already running.
    /// </summary>
    Task<AgentResponse> RunAgentAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an agent by sending a fire-and-forget signal.
    /// Starts the workflow if it is not already running.
    /// Returns immediately without waiting for the agent response.
    /// </summary>
    Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);
}
