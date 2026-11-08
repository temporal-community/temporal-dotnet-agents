using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Activities;

namespace MixedActivities;

public sealed class AgentInvocationActivities(IServiceProvider services)
{
    [Activity]
    public async Task<string> RunAsync(string sessionWorkflowId, string requestId, string message)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        AIAgent agent = services.GetTemporalAgentProxy("DocumentAnalyst");
        var response = await agent.RunAsync(
            message,
            new TemporalAgentSession(sessionId),
            new TemporalAgentRunOptions { RequestId = requestId }).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }
}
