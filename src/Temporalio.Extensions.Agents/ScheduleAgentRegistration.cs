using Temporalio.Client.Schedules;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Represents a scheduled agent run registered via
/// <see cref="TemporalAgentsOptions.AddScheduledAgentRun"/>.
/// Instances are created by the options builder and consumed at worker startup by
/// <see cref="ScheduleRegistrationService"/>.
/// </summary>
internal sealed record ScheduleAgentRegistration(
    string AgentName,
    string ScheduleId,
    RunRequest Request,
    ScheduleSpec Spec,
    SchedulePolicy? Policy = null);
