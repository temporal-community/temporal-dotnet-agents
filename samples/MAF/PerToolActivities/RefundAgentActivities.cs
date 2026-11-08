using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Activities;

namespace PerToolActivities;

public sealed class RefundAgentActivities(IServiceProvider services)
{
    [Activity]
    public async Task<string> RunAsync(
        string sessionWorkflowId,
        string requestId,
        string message,
        bool enableToolCalls,
        IReadOnlyList<string>? enableToolNames)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        AIAgent agent = services.GetTemporalAgentProxy("RefundAgent");
        var response = await agent.RunAsync(
            message,
            new TemporalAgentSession(sessionId),
            new TemporalAgentRunOptions
            {
                EnableToolCalls = enableToolCalls,
                EnableToolNames = enableToolNames?.ToList(),
                RequestId = requestId,
            }).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }
}
