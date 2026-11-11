// Frozen from dc67b065d164ada5ba8987ef7ee4292165246ed2:samples/MAF/AmbientAgent/MonitorStatus.cs.
// Only the namespace and an AmbientAgent import differ; do not modernize this replay fixture.
using AmbientAgent;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests.LegacyAmbientAgent;

/// <summary>
/// Snapshot of monitor workflow state, returned by the <c>GetStatus</c> query.
/// </summary>
public record MonitorStatus(
    int BufferSize,
    int TotalReadings,
    List<string> RecentAnalyses);
