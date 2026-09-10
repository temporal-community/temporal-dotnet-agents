using Temporalio.Common;

namespace TemporalCommunity.Extensions.Agents.Scheduling;

/// <summary>
/// Describes a single deferred agent run to be scheduled from inside an orchestrating workflow
/// via <see cref="ScheduleActivities.ScheduleOneTimeAgentRunAsync"/>.
/// </summary>
/// <remarks>
/// The resulting workflow ID is <c>ta-{agentName}-scheduled-{runId}</c>, which is disjoint
/// from interactive session IDs. If the scheduling activity retries after a crash-before-ack,
/// <see cref="Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.UseExisting"/> protects a running
/// execution and <see cref="Temporalio.Api.Enums.V1.WorkflowIdReusePolicy.RejectDuplicate"/>
/// protects a closed execution. A duplicate start is treated as success.
/// </remarks>
public sealed record OneTimeAgentRun
{
    /// <summary>Gets the name of the agent to run.</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Gets a caller-supplied identifier that uniquely names this deferred run.
    /// Combined with <see cref="AgentName"/> to form the workflow ID:
    /// <c>ta-{agentName}-scheduled-{runId}</c>.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>Gets the request (messages + options) to send to the agent.</summary>
    public required RunRequest Request { get; init; }

    /// <summary>
    /// Gets the wall-clock time at which the agent should run.
    /// If this time is already in the past when the activity executes, the run starts immediately.
    /// </summary>
    public DateTimeOffset RunAt { get; init; }

    /// <summary>
    /// Gets the retry policy applied to the agent activity invocation.
    /// When <see langword="null"/>, the per-agent policy, worker policy, or bounded five-attempt
    /// library default is used, in that order.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
