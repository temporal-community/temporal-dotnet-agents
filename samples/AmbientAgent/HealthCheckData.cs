namespace AmbientAgent;

/// <summary>
/// A single health-check reading from a monitored system.
/// </summary>
public record HealthCheckData(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    double TemperatureCelsius);
