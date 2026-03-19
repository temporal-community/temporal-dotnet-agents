using Temporalio.Activities;
using Temporalio.Client;

namespace AmbientAgent;

/// <summary>
/// Activities for cross-workflow communication. Uses <see cref="ITemporalClient"/>
/// to signal the alert workflow from within the monitor workflow.
/// </summary>
/// <remarks>
/// The Temporal .NET SDK doesn't expose direct workflow-to-workflow signaling.
/// The established pattern (see <c>TemporalAgentContext.SignalWorkflowAsync</c>) routes
/// through an activity with an injected <see cref="ITemporalClient"/>.
/// </remarks>
public class AlertActivities(ITemporalClient client)
{
    [Activity]
    public async Task SignalAlertWorkflowAsync(string alertWorkflowId, AnomalyAlert alert)
    {
        var handle = client.GetWorkflowHandle<AlertWorkflow>(alertWorkflowId);
        await handle.SignalAsync(wf => wf.IngestAnomalyAsync(alert));
    }
}
