using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

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
    private bool _shutdownRequested;

    [WorkflowRun]
    public async Task RunAsync()
    {
        while (!_shutdownRequested)
        {
            // Wait until we have alerts to process, or shutdown is requested.
            var conditionMet = await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _pendingAlerts.Count > 0
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1));

            if (_shutdownRequested)
                break;

            if (Workflow.ContinueAsNewSuggested && _pendingAlerts.Count == 0)
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

                var alertAgent = GetAgent("AlertAgent");
                var session = await alertAgent.CreateSessionAsync();

                var response = await alertAgent.RunAsync(
                    [new ChatMessage(ChatRole.User, prompt)],
                    session);

                var notification = response.Text ?? "(no notification text)";
                _notifications.Add(notification);

                Workflow.Logger.LogInformation("Alert notification dispatched: {Notification}", notification);
            }
        }
    }

    [WorkflowSignal("IngestAnomaly")]
    public Task IngestAnomalyAsync(AnomalyAlert alert)
    {
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
