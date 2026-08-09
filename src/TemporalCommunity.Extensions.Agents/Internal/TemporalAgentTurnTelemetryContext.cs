using System.Diagnostics;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>Per-activity telemetry state shared with the innermost agent boundary.</summary>
internal sealed class TemporalAgentTurnTelemetryContext(string? correlationId)
{
    // These literals intentionally mirror MAF 1.17.0; MAF's matching constants are internal.
    private const string MafOperationNameAttribute = "gen_ai.operation.name";
    private const string MafInvokeAgentOperation = "invoke_agent";

    internal bool EnrichMafInvokeAgentSpan { get; set; }

    internal void EnrichNearestMafInvokeAgentAncestor()
    {
        if (!EnrichMafInvokeAgentSpan)
        {
            return;
        }

        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (string.Equals(
                activity.GetTagItem(MafOperationNameAttribute) as string,
                MafInvokeAgentOperation,
                StringComparison.Ordinal))
            {
                activity.SetTag(
                    TemporalAgentTelemetry.AgentCorrelationIdAttribute,
                    correlationId);
                return;
            }
        }
    }
}
