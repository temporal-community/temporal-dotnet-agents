// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Equivalent to <c>AgentEntity</c> in the DurableTask integration.
/// </summary>
[Workflow("Temporalio.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow
{
    private readonly List<TemporalAgentStateEntry> _history = [];
    private bool _isProcessing;
    private bool _shutdownRequested;
    private AgentWorkflowInput? _input;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        _input = input;

        // Restore history carried forward from a previous run (continue-as-new scenario).
        _history.AddRange(input.CarriedHistory);

        TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

        Logs.LogWorkflowStarted(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId, ttl);

        // Wait until shutdown is requested, TTL elapses, or history is large enough to warrant continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
            timeout: ttl);

        if (!conditionMet)
        {
            // TTL elapsed without condition being met — session complete.
            Logs.LogWorkflowTTLExpired(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId);
        }
        else if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        {
            Logs.LogWorkflowContinueAsNew(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId, _history.Count);

            // Transfer history to a fresh workflow run.
            // Note: collect outside the expression-tree lambda (collection expressions aren't supported there).
            var carriedHistory = _history.ToList();
            throw Workflow.CreateContinueAsNewException(
                (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
                {
                    AgentName = input.AgentName,
                    TaskQueue = input.TaskQueue,
                    TimeToLive = input.TimeToLive,
                    CarriedHistory = carriedHistory,
                    ActivityStartToCloseTimeout = input.ActivityStartToCloseTimeout,
                    ActivityHeartbeatTimeout = input.ActivityHeartbeatTimeout
                }));
        }
    }

    /// <summary>
    /// Runs the agent with the given request and returns the response.
    /// Updates are serialized — only one runs at a time.
    /// </summary>
    [WorkflowUpdate("Run")]
    public async Task<AgentResponse> RunAgentAsync(RunRequest request)
    {
        // Serialize: wait for any in-progress run to finish first.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        Logs.LogWorkflowUpdateReceived(Workflow.Logger, _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);

        try
        {
            _history.Add(TemporalAgentStateRequest.FromRunRequest(request));

            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                [.. _history]);

            var response = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5)
                });

            _history.Add(TemporalAgentStateResponse.FromResponse(request.CorrelationId, response));

            Logs.LogWorkflowUpdateCompleted(Workflow.Logger, _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);
            return response;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Queues a fire-and-forget run. The workflow does not wait for this to complete.
    /// </summary>
    [WorkflowSignal("RunFireAndForget")]
    public Task RunAgentFireAndForgetAsync(RunRequest request)
    {
        _ = ProcessFireAndForgetAsync(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests graceful shutdown of this workflow.
    /// </summary>
    [WorkflowSignal("Shutdown")]
    public Task RequestShutdownAsync()
    {
        Logs.LogWorkflowShutdownRequested(Workflow.Logger, _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId);
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the current conversation history.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<TemporalAgentStateEntry> GetHistory() => _history;

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;
        try
        {
            _history.Add(TemporalAgentStateRequest.FromRunRequest(request));

            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                [.. _history]);

            var response = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5)
                });

            _history.Add(TemporalAgentStateResponse.FromResponse(request.CorrelationId, response));
        }
        finally
        {
            _isProcessing = false;
        }
    }
}
