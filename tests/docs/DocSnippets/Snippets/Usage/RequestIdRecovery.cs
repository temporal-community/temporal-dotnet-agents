// HARNESS for docs/how-to/MAF/usage.md § "Request ID Recovery".
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;

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

    internal static async Task CancelAsync(
        IServiceProvider services,
        AIAgent agentProxy,
        TemporalAgentSession session)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#cancel-an-active-turn
        var agentClient = services.GetRequiredService<ITemporalAgentClient>();
        var requestId = Guid.NewGuid().ToString("N"); // Persist before submission.
        var turn = agentProxy.RunAsync(
            "Run the long operation.",
            session,
            new TemporalAgentRunOptions { RequestId = requestId });

        var cancellation = await agentClient.CancelTurnAsync(session.SessionId, requestId);
        if (cancellation.Status == DurableTurnCancellationStatus.CancellationRequested)
        {
            Console.WriteLine("The workflow requested cancellation.");
        }
        // END SNIPPET docs/how-to/MAF/usage.md#cancel-an-active-turn
        _ = turn;
    }
}
