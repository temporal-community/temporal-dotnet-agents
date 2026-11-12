using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;

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
    private readonly Queue<HealthCheckData> _pendingAnalysisReadings = new();
    private readonly Queue<string> _recentAnalyses = new();

    // Static readonly avoids allocating ActivityOptions on every loop iteration.
    private static readonly ActivityOptions AlertActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    private int _totalReadings;
    private int _legacyReadingsSinceLastAnalysis;
    private bool _legacyInputInitialized;
    private readonly bool _useFifoDrain;
    private bool _shutdownRequested;
    private readonly MonitorWorkflowInput _input;

    [WorkflowInit]
    public MonitorWorkflow(MonitorWorkflowInput input)
    {
        _input = input;
        _useFifoDrain = Workflow.Patched("ambient-monitor-fifo-drain-v1");
        if (!_useFifoDrain)
            return;

        // Restore state carried forward from a previous run (continue-as-new).
        foreach (var reading in input.CarriedBuffer)
            _buffer.Enqueue(reading);
        foreach (var reading in input.CarriedPendingAnalysisReadings)
            _pendingAnalysisReadings.Enqueue(reading);
        if (input.CarriedPendingAnalysisReadings.Count == 0
            && input.CarriedReadingsSinceLastAnalysis > 0)
        {
            foreach (var reading in _buffer.TakeLast(input.CarriedReadingsSinceLastAnalysis))
                _pendingAnalysisReadings.Enqueue(reading);
        }
        _totalReadings = input.CarriedTotalReadings;
    }

    [WorkflowRun]
    public async Task RunAsync(MonitorWorkflowInput input)
    {
        if (!_useFifoDrain)
        {
            await RunLegacyAsync(input).ConfigureAwait(true);
            return;
        }

        while (true)
        {
            // Wait until we have enough new readings for an analysis pass, or shutdown.
            // Pass Workflow.CancellationToken so the wait is cancelled cleanly on shutdown.
            await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _pendingAnalysisReadings.Count >= input.AnalysisInterval
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1),
                cancellationToken: Workflow.CancellationToken)
                .ConfigureAwait(true);

            if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
            {
                throw Workflow.CreateContinueAsNewException(
                    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = input.AlertWorkflowId,
                        AnalysisInterval = input.AnalysisInterval,
                        MaxBufferSize = input.MaxBufferSize,
                        CarriedBuffer = _buffer.ToList(),
                        CarriedTotalReadings = _totalReadings,
                        CarriedReadingsSinceLastAnalysis = _pendingAnalysisReadings.Count,
                        CarriedPendingAnalysisReadings = _pendingAnalysisReadings.ToList()
                    }));
            }

            if (_pendingAnalysisReadings.Count < input.AnalysisInterval)
            {
                if (_shutdownRequested)
                    break;

                continue;
            }

            // ── Analyze recent readings via LLM ──────────────────────────────
            var batch = new List<HealthCheckData>(input.AnalysisInterval);
            for (var i = 0; i < input.AnalysisInterval; i++)
                batch.Add(_pendingAnalysisReadings.Dequeue());

            var summary = FormatReadingsForAnalysis(batch);
            var sessionId = WorkflowAgents.NewAgentSessionId("AnalysisAgent");
            var analysisResult = await Workflow.ExecuteActivityAsync(
                (AgentInvocationActivities activities) => activities.RunAsync(
                    "AnalysisAgent", sessionId.WorkflowId, Workflow.NewGuid().ToString("N"), summary),
                AgentInvocationOptions.Default).ConfigureAwait(true);
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
                    RecentReadings: batch);

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
        if (_useFifoDrain && _shutdownRequested)
            return Task.CompletedTask;

        _buffer.Enqueue(data);
        if (_useFifoDrain)
            _pendingAnalysisReadings.Enqueue(data);
        else
            _legacyReadingsSinceLastAnalysis++;
        _totalReadings++;

        // Enforce max buffer size — drop oldest readings.
        while ((_useFifoDrain || _legacyInputInitialized) && _buffer.Count > _input.MaxBufferSize)
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
        new(_buffer.Count, _totalReadings, _recentAnalyses.ToList(),
            _useFifoDrain ? _pendingAnalysisReadings.Count : _legacyReadingsSinceLastAnalysis);

    // Keep the original command and signal behavior for histories without the FIFO patch.
    private async Task RunLegacyAsync(MonitorWorkflowInput input)
    {
        _legacyInputInitialized = true;
        foreach (var reading in input.CarriedBuffer)
            _buffer.Enqueue(reading);
        _totalReadings = input.CarriedTotalReadings;
        _legacyReadingsSinceLastAnalysis = input.CarriedReadingsSinceLastAnalysis;

        while (!_shutdownRequested)
        {
            var conditionMet = await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _legacyReadingsSinceLastAnalysis >= input.AnalysisInterval
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1),
                cancellationToken: Workflow.CancellationToken).ConfigureAwait(true);

            if (_shutdownRequested)
                break;

            if (Workflow.ContinueAsNewSuggested && _legacyReadingsSinceLastAnalysis < input.AnalysisInterval)
            {
                throw Workflow.CreateContinueAsNewException(
                    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = input.AlertWorkflowId,
                        AnalysisInterval = input.AnalysisInterval,
                        MaxBufferSize = input.MaxBufferSize,
                        CarriedBuffer = _buffer.ToList(),
                        CarriedTotalReadings = _totalReadings,
                        CarriedReadingsSinceLastAnalysis = _legacyReadingsSinceLastAnalysis
                    }));
            }

            if (!conditionMet || _legacyReadingsSinceLastAnalysis < input.AnalysisInterval)
                continue;

            _legacyReadingsSinceLastAnalysis = 0;
            var summary = FormatReadingsForAnalysis(_buffer.TakeLast(input.AnalysisInterval));
            var sessionId = WorkflowAgents.NewAgentSessionId("AnalysisAgent");
            var analysisResult = await Workflow.ExecuteActivityAsync(
                (AgentInvocationActivities activities) => activities.RunAsync(
                    "AnalysisAgent", sessionId.WorkflowId, Workflow.NewGuid().ToString("N"), summary),
                AgentInvocationOptions.Default).ConfigureAwait(true);
            _recentAnalyses.Enqueue(analysisResult);
            while (_recentAnalyses.Count > 10)
                _recentAnalyses.Dequeue();

            Workflow.Logger.LogInformation(
                "Analysis complete ({TotalReadings} total readings): {Result}",
                _totalReadings, analysisResult);

            if (analysisResult.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase))
            {
                var alert = new AnomalyAlert(
                    Workflow.UtcNow, analysisResult, _buffer.TakeLast(input.AnalysisInterval).ToList());
                await Workflow.ExecuteActivityAsync(
                    (AlertActivities activities) => activities.SignalAlertWorkflowAsync(input.AlertWorkflowId, alert),
                    AlertActivityOptions).ConfigureAwait(true);
            }
        }
    }

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
