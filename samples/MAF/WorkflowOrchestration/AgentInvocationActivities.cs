using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Activities;

namespace WorkflowOrchestration;

/// <summary>Invokes a registered Temporal agent proxy from activity context.</summary>
public sealed class AgentInvocationActivities(IServiceProvider services)
{
    [Activity]
    public async Task<string> RunAsync(string sessionWorkflowId, string requestId, string message)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        AIAgent agent = services.GetTemporalAgentProxy("WeatherAssistant");
        var session = new TemporalAgentSession(sessionId);
        var response = await agent.RunAsync(
            message,
            session,
            new TemporalAgentRunOptions { RequestId = requestId }).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    [Activity]
    public async Task StartFireAndForgetAsync(string sessionWorkflowId, string requestId, string message)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        AIAgent agent = services.GetTemporalAgentProxy("WeatherAssistant");
        var session = new TemporalAgentSession(sessionId);
        await agent.RunAsync(
            message,
            session,
            new TemporalAgentRunOptions
            {
                IsFireAndForget = true,
                RequestId = requestId,
            }).ConfigureAwait(false);
    }
}
