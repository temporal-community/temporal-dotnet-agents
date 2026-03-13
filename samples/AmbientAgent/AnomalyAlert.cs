namespace AmbientAgent;

/// <summary>
/// An anomaly detected by the analysis agent, sent to the alert workflow for notification.
/// </summary>
public record AnomalyAlert(
    DateTimeOffset DetectedAt,
    string Summary,
    List<HealthCheckData> RecentReadings);
