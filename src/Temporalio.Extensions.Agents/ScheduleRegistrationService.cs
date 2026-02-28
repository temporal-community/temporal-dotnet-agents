using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Exceptions;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// A background service that registers configured scheduled agent runs with Temporal at worker startup.
/// Runs after the DI container is fully built and <see cref="ITemporalAgentClient"/> is available.
/// </summary>
/// <remarks>
/// <para>
/// If a schedule already exists (e.g. on subsequent worker restarts), the service logs a warning
/// and skips creation — it does <b>not</b> overwrite or update the existing schedule.
/// </para>
/// <para>
/// <b>Config drift:</b> if you change a schedule's spec in code (e.g. from daily to twice-daily),
/// the change is silently ignored because the existing schedule is skipped. To apply the updated
/// spec, delete the schedule first via <see cref="ITemporalAgentClient.GetAgentScheduleHandle"/>
/// and then restart the worker.
/// </para>
/// </remarks>
internal sealed class ScheduleRegistrationService(
    ITemporalAgentClient agentClient,
    TemporalAgentsOptions options,
    ILogger<ScheduleRegistrationService>? logger = null) : BackgroundService
{
    private readonly ILogger<ScheduleRegistrationService> _logger =
        logger ?? NullLogger<ScheduleRegistrationService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registrations = options.GetScheduledRuns();
        if (registrations.Count == 0)
        {
            return;
        }

        foreach (var registration in registrations)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                await agentClient.ScheduleAgentAsync(
                    registration.AgentName,
                    registration.ScheduleId,
                    registration.Request,
                    registration.Spec,
                    registration.Policy,
                    stoppingToken);

                _logger.LogScheduleCreated(registration.ScheduleId, registration.AgentName);
            }
            catch (ScheduleAlreadyRunningException)
            {
                _logger.LogScheduleAlreadyExists(registration.ScheduleId, registration.AgentName);
            }
        }
    }
}
