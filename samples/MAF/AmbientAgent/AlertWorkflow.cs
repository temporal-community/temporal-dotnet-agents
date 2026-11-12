using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;

namespace AmbientAgent;

/// <summary>
/// Receives anomaly alerts from the monitor workflow and uses an LLM agent
/// to compose human-readable notifications. Supports continue-as-new for
/// indefinite operation.
/// </summary>
[Workflow("AmbientAgent.AlertWorkflow")]
public class AlertWorkflow
{
    private readonly List<AnomalyAlert> _pendingAlerts = [];
    private readonly List<string> _notifications = [];
    private readonly bool _drainOnShutdown;
    private bool _shutdownRequested;

    [WorkflowInit]
    public AlertWorkflow() => _drainOnShutdown = Workflow.Patched("ambient-alert-drain-v1");

    [WorkflowRun]
    public async Task RunAsync()
    {
        while (true)
        {
            // Wait until we have alerts to process, or shutdown is requested.
            // Pass Workflow.CancellationToken so the wait is cancelled cleanly on shutdown.
            await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _pendingAlerts.Count > 0
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1),
                cancellationToken: Workflow.CancellationToken)
                .ConfigureAwait(true);

            if (_shutdownRequested && (!_drainOnShutdown || _pendingAlerts.Count == 0))
                break;

            if (Workflow.ContinueAsNewSuggested && _pendingAlerts.Count == 0 && !_shutdownRequested)
            {
                // Notifications are ephemeral — no need to carry them forward.
                throw Workflow.CreateContinueAsNewException(
                    (AlertWorkflow wf) => wf.RunAsync());
            }

            if (_pendingAlerts.Count == 0)
                continue;

            // Process all pending alerts.
            var alertsToProcess = _pendingAlerts.ToList();
            _pendingAlerts.Clear();

            foreach (var alert in alertsToProcess)
            {
                var prompt = $"Compose a concise alert notification for the following anomaly:\n\n" +
                             $"Detected at: {alert.DetectedAt:u}\n" +
                             $"Analysis: {alert.Summary}\n\n" +
                             $"Recent readings:\n" +
                             string.Join("\n", alert.RecentReadings.Select(r =>
                                 $"  [{r.Timestamp:HH:mm:ss}] CPU={r.CpuPercent:F1}% Mem={r.MemoryPercent:F1}% Temp={r.TemperatureCelsius:F1}°C"));

                var sessionId = WorkflowAgents.NewAgentSessionId("AlertAgent");
                var notification = await Workflow.ExecuteActivityAsync(
                    (AgentInvocationActivities activities) => activities.RunAsync(
                        "AlertAgent", sessionId.WorkflowId, Workflow.NewGuid().ToString("N"), prompt),
                    AgentInvocationOptions.Default).ConfigureAwait(true);

                notification = string.IsNullOrEmpty(notification) ? "(no notification text)" : notification;
                _notifications.Add(notification);

                Workflow.Logger.LogInformation("Alert notification dispatched: {Notification}", notification);
            }
        }
    }

    [WorkflowSignal("IngestAnomaly")]
    public Task IngestAnomalyAsync(AnomalyAlert alert)
    {
        if (_drainOnShutdown && _shutdownRequested)
            return Task.CompletedTask;

        _pendingAlerts.Add(alert);
        return Task.CompletedTask;
    }

    [WorkflowSignal("Shutdown")]
    public Task ShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery("GetNotifications")]
    public List<string> GetNotifications() => _notifications.ToList();
}
