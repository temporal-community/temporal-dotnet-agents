// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Default implementation of <see cref="ITemporalAgentClient"/> that communicates with
/// <see cref="AgentWorkflow"/> via Temporal workflow updates (no polling).
/// </summary>
internal class DefaultTemporalAgentClient(
    ITemporalClient client,
    TemporalAgentsOptions options,
    string taskQueue,
    ILogger<DefaultTemporalAgentClient>? logger = null) : ITemporalAgentClient
{
    private readonly ITemporalClient _client = client;
    private readonly TemporalAgentsOptions _options = options;
    private readonly string _taskQueue = taskQueue;
    private readonly ILogger<DefaultTemporalAgentClient> _logger =
        logger ?? NullLogger<DefaultTemporalAgentClient>.Instance;

    /// <inheritdoc/>
    public async Task<AgentResponse> RunAgentAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, _taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        Logs.LogClientSendingUpdate(_logger, sessionId.AgentName, sessionId.WorkflowId);

        // Ensure the workflow exists (starts a new one, or no-ops if one is already running).
        await _client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
            {
                AgentName = sessionId.AgentName,
                TaskQueue = _taskQueue,
                TimeToLive = _options.GetTimeToLive(sessionId.AgentName),
                ActivityStartToCloseTimeout = _options.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = _options.ActivityHeartbeatTimeout
            }),
            workflowOptions);

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        // The handle from StartWorkflowAsync pins to a specific run, which becomes stale
        // if the workflow has done continue-as-new since it was first started.
        var handle = _client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        // Execute the update — returns the response directly, no polling needed.
        var response = await handle.ExecuteUpdateAsync<AgentWorkflow, AgentResponse>(
            wf => wf.RunAgentAsync(request));

        Logs.LogClientUpdateCompleted(_logger, sessionId.AgentName, sessionId.WorkflowId);
        return response;
    }

    /// <inheritdoc/>
    public async Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, _taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        Logs.LogClientFireAndForget(_logger, sessionId.AgentName, sessionId.WorkflowId);

        // Ensure the workflow exists.
        await _client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
            {
                AgentName = sessionId.AgentName,
                TaskQueue = _taskQueue,
                TimeToLive = _options.GetTimeToLive(sessionId.AgentName),
                ActivityStartToCloseTimeout = _options.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = _options.ActivityHeartbeatTimeout
            }),
            workflowOptions);

        // Use an unpinned handle so signals follow the continue-as-new chain.
        var handle = _client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.SignalAsync<AgentWorkflow>(wf => wf.RunAgentFireAndForgetAsync(request));
    }
}
