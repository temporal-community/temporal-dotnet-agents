namespace AmbientAgent;

/// <summary>
/// Snapshot of monitor workflow state, returned by the <c>GetStatus</c> query.
/// </summary>
public record MonitorStatus(
    int BufferSize,
    int TotalReadings,
    List<string> RecentAnalyses);
