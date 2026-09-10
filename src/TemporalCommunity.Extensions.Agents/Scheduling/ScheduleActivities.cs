using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Workflows;

namespace TemporalCommunity.Extensions.Agents.Scheduling;

/// <summary>
/// Temporal activities for scheduling deferred, one-time agent runs from inside orchestrating workflows.
/// </summary>
/// <remarks>
/// <para>
/// Use this from inside a <c>[WorkflowRun]</c> method when you want to schedule a future agent
/// invocation without blocking the current workflow:
/// </para>
/// <code>
/// await Workflow.ExecuteActivityAsync(
///     (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
///     {
///         AgentName = "ResearchAnalyst",
///         RunId     = "followup-q1",
///         Request   = new RunRequest("Compare today's data against last week's findings."),
///         RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7)
///     }),
///     new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
/// </code>
/// <para>
/// Internally this uses <c>StartDelay</c> on <see cref="ITemporalClient.StartWorkflowAsync"/>,
/// which leaves a single visible workflow execution in the Temporal Web UI rather than a
/// persistent schedule entity. This avoids zombie schedules after the single run completes.
/// </para>
/// </remarks>
public sealed class ScheduleActivities(ITemporalClient client, string taskQueue, TemporalAgentsOptions options)
{
    private readonly ILogger<ScheduleActivities> _logger = NullLogger<ScheduleActivities>.Instance;

    /// <summary>
    /// Logger-aware overload used by the worker registrar. The public three-argument constructor
    /// stays available for direct construction and gets a <see cref="NullLogger{T}"/>.
    /// </summary>
    internal ScheduleActivities(
        ITemporalClient client,
        string taskQueue,
        TemporalAgentsOptions options,
        ILogger<ScheduleActivities>? logger)
        : this(client, taskQueue, options)
    {
        _logger = logger ?? NullLogger<ScheduleActivities>.Instance;
    }

    /// <summary>
    /// Schedules a one-time, deferred <see cref="AgentJobWorkflow"/> run.
    /// </summary>
    /// <param name="run">Describes the agent, run identifier, request, and target time.</param>
    /// <remarks>
    /// <para>
    /// The resulting workflow ID is <c>ta-{agentName}-scheduled-{runId}</c>. If the activity
    /// retries after a crash-before-ack, the workflow ID is protected by both
    /// <c>UseExisting</c> conflict policy for a running execution and <c>RejectDuplicate</c>
    /// reuse policy for a closed execution. A duplicate start is treated as success.
    /// </para>
    /// <para>
    /// If <see cref="OneTimeAgentRun.RunAt"/> is in the past when this activity executes,
    /// the agent run starts immediately (delay clamped to zero).
    /// </para>
    /// </remarks>
    [Activity]
    public async Task ScheduleOneTimeAgentRunAsync(OneTimeAgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var delay = run.RunAt - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleOneTimeSpanName,
            ActivityKind.Internal);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, run.AgentName);
        span?.SetTag(TemporalAgentTelemetry.ScheduleJobIdAttribute, run.RunId);
        span?.SetTag(TemporalAgentTelemetry.ScheduleDelayAttribute, delay.ToString());

        var workflowId = $"ta-{run.AgentName.ToLowerInvariant()}-scheduled-{run.RunId}";

        // Build the full AgentJobInput the same way ScheduleAgentAsync does, so per-agent
        // timeouts, per-tool options, and interceptor config are respected (P2 fix).
        var jobInput = DefaultTemporalAgentClient.BuildAgentJobInput(
            run.AgentName, run.Request, options, taskQueue, run.RetryPolicy);

        try
        {
            await client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
                new WorkflowOptions(workflowId, taskQueue)
                {
                    StartDelay = delay,
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                }).ConfigureAwait(false);
        }
        catch (WorkflowAlreadyStartedException)
        {
            // The workflow ID is the idempotency key. A prior activity attempt may have started
            // and even completed the job before its completion was recorded by the caller.
            // RejectDuplicate closes that post-completion retry window; seeing the rejection means
            // the requested one-time run already exists and this activity has succeeded.
            //
            // Identifiers only. The RunRequest (prompt, messages, tool payloads) must never be
            // logged here — this line exists so an operator can tell a deduplicated retry apart
            // from a run that never started, and that needs nothing but the idempotency key.
            _logger.LogScheduleOneTimeDuplicateIgnored(run.AgentName, run.RunId, workflowId);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
