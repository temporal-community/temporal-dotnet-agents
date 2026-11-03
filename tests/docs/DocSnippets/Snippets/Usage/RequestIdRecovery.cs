// HARNESS for docs/how-to/MAF/usage.md § "Request ID Recovery".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;

namespace DocSnippets.Usage;

internal static class RequestIdRecovery
{
    internal static async Task<AgentResponse> SendAsync(AIAgent agentProxy, AgentSession session)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
        var requestId = Guid.NewGuid().ToString("N"); // Persist this ID before submitting the turn.
        var response = await agentProxy.RunAsync(
            "What is the capital of France?",
            session,
            new TemporalAgentRunOptions { RequestId = requestId });
        // END SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
        return response;
    }
}
