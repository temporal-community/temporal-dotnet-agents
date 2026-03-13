using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace AmbientAgent;

/// <summary>
/// Ambient monitoring workflow that ingests health-check signals, periodically
/// calls an LLM to analyze trends, and signals an alert workflow on anomalies.
/// Supports continue-as-new for indefinite operation.
/// </summary>
[Workflow("AmbientAgent.MonitorWorkflow")]
public class MonitorWorkflow
{
    private readonly List<HealthCheckData> _buffer = [];
    private readonly List<string> _recentAnalyses = [];

    private int _totalReadings;
    private int _readingsSinceLastAnalysis;
    private bool _shutdownRequested;
    private MonitorWorkflowInput _input = null!;

    [WorkflowRun]
    public async Task RunAsync(MonitorWorkflowInput input)
    {
        _input = input;

        // Restore state carried forward from a previous run (continue-as-new).
        _buffer.AddRange(input.CarriedBuffer);
        _totalReadings = input.CarriedTotalReadings;

        while (!_shutdownRequested)
        {
            // Wait until we have enough new readings for an analysis pass, or shutdown.
            var conditionMet = await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _readingsSinceLastAnalysis >= input.AnalysisInterval
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1));

            if (_shutdownRequested)
                break;

            // If history is too large, continue-as-new with carried state.
            if (Workflow.ContinueAsNewSuggested)
            {
                throw Workflow.CreateContinueAsNewException(
                    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = input.AlertWorkflowId,
                        AnalysisInterval = input.AnalysisInterval,
                        MaxBufferSize = input.MaxBufferSize,
                        CarriedBuffer = _buffer.ToList(),
                        CarriedTotalReadings = _totalReadings
                    }));
            }

            if (!conditionMet)
                continue; // Timeout with no readings — loop back and wait again.

            if (_readingsSinceLastAnalysis < input.AnalysisInterval)
                continue;

            // ── Analyze recent readings via LLM ──────────────────────────────
            _readingsSinceLastAnalysis = 0;

            var summary = FormatReadingsForAnalysis(_buffer);
            var analysisAgent = GetAgent("AnalysisAgent");
            var session = await analysisAgent.CreateSessionAsync();

            var response = await analysisAgent.RunAsync(
                [new ChatMessage(ChatRole.User, summary)],
                session);

            var analysisResult = response.Text ?? string.Empty;
            _recentAnalyses.Add(analysisResult);

            // Keep only the last 10 analyses in memory.
            while (_recentAnalyses.Count > 10)
                _recentAnalyses.RemoveAt(0);

            Workflow.Logger.LogInformation(
                "Analysis complete ({TotalReadings} total readings): {Result}",
                _totalReadings, analysisResult);

            // ── Check for anomaly and signal alert workflow ───────────────────
            if (analysisResult.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase))
            {
                var alert = new AnomalyAlert(
                    DetectedAt: Workflow.UtcNow,
                    Summary: analysisResult,
                    RecentReadings: _buffer.TakeLast(input.AnalysisInterval).ToList());

                await Workflow.ExecuteActivityAsync(
                    (AlertActivities a) => a.SignalAlertWorkflowAsync(input.AlertWorkflowId, alert),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
            }
        }
    }

    [WorkflowSignal("IngestHealthCheck")]
    public Task IngestHealthCheckAsync(HealthCheckData data)
    {
        _buffer.Add(data);
        _totalReadings++;
        _readingsSinceLastAnalysis++;

        // Enforce max buffer size — drop oldest readings.
        while (_buffer.Count > _input.MaxBufferSize)
            _buffer.RemoveAt(0);

        return Task.CompletedTask;
    }

    [WorkflowSignal("Shutdown")]
    public Task ShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery("GetStatus")]
    public MonitorStatus GetStatus() =>
        new(_buffer.Count, _totalReadings, _recentAnalyses.ToList());

    private static string FormatReadingsForAnalysis(List<HealthCheckData> readings)
    {
        var lines = readings.Select(r =>
            $"[{r.Timestamp:HH:mm:ss}] CPU={r.CpuPercent:F1}% Mem={r.MemoryPercent:F1}% Temp={r.TemperatureCelsius:F1}°C");

        return $"Analyze these system health readings and determine if there are anomalies.\n" +
               $"Respond with 'NORMAL: <brief summary>' if everything looks fine, " +
               $"or 'ANOMALY: <description>' if you detect concerning patterns.\n\n" +
               string.Join("\n", lines);
    }
}
