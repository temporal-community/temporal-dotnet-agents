using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

namespace AmbientAgent;

/// <summary>
/// Ambient monitoring workflow that ingests health-check signals, periodically
/// calls an LLM to analyze trends, and signals an alert workflow on anomalies.
/// Supports continue-as-new for indefinite operation.
/// </summary>
[Workflow("AmbientAgent.MonitorWorkflow")]
public class MonitorWorkflow
{
    // Queue<T> gives O(1) enqueue/dequeue vs. List<T>.RemoveAt(0)'s O(n).
    private readonly Queue<HealthCheckData> _buffer = new();
    private readonly Queue<string> _recentAnalyses = new();

    // Static readonly avoids allocating ActivityOptions on every loop iteration.
    private static readonly ActivityOptions AlertActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    private int _totalReadings;
    private int _readingsSinceLastAnalysis;
    private bool _shutdownRequested;
    private MonitorWorkflowInput _input = null!;

    [WorkflowRun]
    public async Task RunAsync(MonitorWorkflowInput input)
    {
        _input = input;

        // Restore state carried forward from a previous run (continue-as-new).
        foreach (var reading in input.CarriedBuffer)
            _buffer.Enqueue(reading);
        _totalReadings = input.CarriedTotalReadings;
        _readingsSinceLastAnalysis = input.CarriedReadingsSinceLastAnalysis;

        while (!_shutdownRequested)
        {
            // Wait until we have enough new readings for an analysis pass, or shutdown.
            // Pass Workflow.CancellationToken so the wait is cancelled cleanly on shutdown.
            var conditionMet = await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _readingsSinceLastAnalysis >= input.AnalysisInterval
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1),
                cancellationToken: Workflow.CancellationToken)
                .ConfigureAwait(true);

            if (_shutdownRequested)
                break;

            // Defer continue-as-new when a full analysis batch is ready — otherwise
            // the pending analysis would be silently dropped on the next run start.
            if (Workflow.ContinueAsNewSuggested && _readingsSinceLastAnalysis < input.AnalysisInterval)
            {
                // Carry _readingsSinceLastAnalysis forward so the restored run does
                // not re-count readings that were already counted before the transition.
                throw Workflow.CreateContinueAsNewException(
                    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = input.AlertWorkflowId,
                        AnalysisInterval = input.AnalysisInterval,
                        MaxBufferSize = input.MaxBufferSize,
                        CarriedBuffer = _buffer.ToList(),
                        CarriedTotalReadings = _totalReadings,
                        CarriedReadingsSinceLastAnalysis = _readingsSinceLastAnalysis
                    }));
            }

            // On timeout (conditionMet == false), the interval check below would also
            // be false — both guards point to the same "not enough readings yet" situation.
            // The first guard handles the timeout path; the second makes the intent explicit
            // for the non-timeout path where ContinueAsNewSuggested unblocked the wait but
            // the interval has not been reached yet.
            if (!conditionMet)
                continue; // Timeout with no readings — loop back and wait again.

            if (_readingsSinceLastAnalysis < input.AnalysisInterval)
                continue;

            // ── Analyze recent readings via LLM ──────────────────────────────
            _readingsSinceLastAnalysis = 0;

            // Analyze only the most recent AnalysisInterval readings, not the full buffer.
            var summary = FormatReadingsForAnalysis(_buffer.TakeLast(input.AnalysisInterval));
            var analysisAgent = GetTemporalAgent("AnalysisAgent");

            // Fresh session per cycle: each LLM call is a stateless analysis of the current window.
            // To accumulate cross-cycle conversation history, store the session in a field and reuse it.
            var session = await analysisAgent.CreateSessionAsync().ConfigureAwait(true);

            var response = await analysisAgent.RunAsync(
                [new ChatMessage(ChatRole.User, summary)],
                session).ConfigureAwait(true);

            var analysisResult = response.Text ?? string.Empty;
            _recentAnalyses.Enqueue(analysisResult);

            // Keep only the last 10 analyses in memory.
            while (_recentAnalyses.Count > 10)
                _recentAnalyses.Dequeue();

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
                    AlertActivityOptions) // reuse static instance
                    .ConfigureAwait(true);
            }
        }
    }

    [WorkflowSignal("IngestHealthCheck")]
    public Task IngestHealthCheckAsync(HealthCheckData data)
    {
        _buffer.Enqueue(data);
        _totalReadings++;
        _readingsSinceLastAnalysis++;

        // Enforce max buffer size — drop oldest readings.
        // Guard against _input being null if a signal arrives before the first workflow task.
        while (_input is not null && _buffer.Count > _input.MaxBufferSize)
            _buffer.Dequeue();

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

    // agent.Instructions already tells the model how to respond, so the user message
    // here contains only the data to analyze — no duplicated format instructions.
    private static string FormatReadingsForAnalysis(IEnumerable<HealthCheckData> readings)
    {
        var lines = readings.Select(r =>
            $"[{r.Timestamp:HH:mm:ss}] CPU={r.CpuPercent:F1}% Mem={r.MemoryPercent:F1}% Temp={r.TemperatureCelsius:F1}°C");

        return $"Analyze these system health readings:\n\n" +
               string.Join("\n", lines);
    }
}
