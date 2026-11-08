// RoutingWorkflow — workflow-based routing:
//   A RoutingActivities.ClassifyRequest activity picks the right specialist from
//   a keyword analysis of the user's question. The decision is recorded in event
//   history and is therefore durable, retryable, and visible in the Temporal Web UI.
//
// ParallelAgentWorkflow — parallel fan-out:
//   Sends the same query to all three specialists simultaneously via
//   separate activities that invoke their registered agent proxies.

using TemporalCommunity.Extensions.Agents;
using Temporalio.Workflows;

namespace MultiAgentRouting;

/// <summary>
/// Routes a user question to the appropriate specialist agent.
/// The routing decision runs inside a <see cref="RoutingActivities.ClassifyRequest"/>
/// activity so it is recorded in event history — durable, retryable, and auditable.
/// </summary>
[Workflow("MultiAgentRouting.RoutingWorkflow")]
public class RoutingWorkflow
{
    private static readonly ActivityOptions ClassifyActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Receives a user question, classifies intent via an activity, and routes to the
    /// appropriate specialist agent.
    /// </summary>
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // ── Step 1: Classify the intent via activity ─────────────────────────
        // Running classification in an activity means:
        //   • The result is cached in workflow history — a crash after this point
        //     won't re-invoke the classifier.
        //   • The routing decision is visible in the Temporal Web UI event log.
        //   • Retries apply if the classification activity fails transiently.
        var agentName = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.ClassifyRequest(userQuestion),
            ClassifyActivityOptions).ConfigureAwait(true);

        // ── Step 2: Dispatch to the chosen specialist ────────────────────────
        var sessionId = WorkflowAgents.NewAgentSessionId(agentName);
        return await Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                agentName,
                sessionId.WorkflowId,
                Workflow.NewGuid().ToString("N"),
                userQuestion),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) }).ConfigureAwait(true);
    }
}

/// <summary>
/// Fans out the same query to all three specialist agents in parallel and returns
/// all of their responses. Agent proxies are invoked by independent activities.
/// </summary>
[Workflow("MultiAgentRouting.ParallelAgentWorkflow")]
public class ParallelAgentWorkflow
{
    /// <summary>
    /// Sends <paramref name="userQuery"/> to all three specialist agents in parallel
    /// and returns their responses.
    /// </summary>
    [WorkflowRun]
    public async Task<string[]> RunAsync(string userQuery)
    {
        var weatherSession = WorkflowAgents.NewAgentSessionId("WeatherAgent");
        var billingSession = WorkflowAgents.NewAgentSessionId("BillingAgent");
        var techSupportSession = WorkflowAgents.NewAgentSessionId("TechSupportAgent");

        var weather = Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                "WeatherAgent", weatherSession.WorkflowId, Workflow.NewGuid().ToString("N"), userQuery),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
        var billing = Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                "BillingAgent", billingSession.WorkflowId, Workflow.NewGuid().ToString("N"), userQuery),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
        var techSupport = Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                "TechSupportAgent", techSupportSession.WorkflowId, Workflow.NewGuid().ToString("N"), userQuery),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });

        return await Workflow.WhenAllAsync([weather, billing, techSupport]).ConfigureAwait(true);
    }
}
