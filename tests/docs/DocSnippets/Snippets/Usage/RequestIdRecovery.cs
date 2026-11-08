// HARNESS for docs/how-to/MAF/usage.md § "Request ID Recovery".
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI;
using Temporalio.Exceptions;

namespace DocSnippets.Usage;

internal static class RequestIdRecovery
{
    internal static async Task<AgentResponse> SendAsync(AIAgent agentProxy, string receiptPath)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
        var session = (TemporalAgentSession)await agentProxy.CreateSessionAsync();
        var requestId = Guid.NewGuid().ToString("N");
        await using (var file = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(file, new
            {
                SessionWorkflowId = session.SessionId.WorkflowId,
                RequestId = requestId,
            });
            file.Flush(flushToDisk: true);
        }
        var response = await agentProxy.RunAsync(
            "What is the capital of France?",
            session,
            new TemporalAgentRunOptions { RequestId = requestId });
        // END SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
        return response;
    }

    internal static async Task ReconcileAsync(
        IServiceProvider serviceProvider,
        string receiptPath)
    {
        // BEGIN SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
        var sessionId = TemporalAgentSessionId.Parse(receipt.RootElement.GetProperty("SessionWorkflowId").GetString()!);
        var requestId = receipt.RootElement.GetProperty("RequestId").GetString()!;
        var temporalAgentClient = serviceProvider.GetRequiredService<ITemporalAgentClient>();
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            TemporalAgentTurnOutcome outcome;
            try
            {
                using var queryWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                outcome = await temporalAgentClient.GetTurnOutcomeAsync(sessionId, requestId, queryWait.Token);
            }
            catch (Exception error) when (error is RpcException or WorkflowQueryFailedException
                or WorkflowQueryRejectedException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Query failed; no outcome established: {error.Message}");
                throw;
            }
            if (outcome.Status != TemporalAgentTurnOutcomeStatus.Pending)
            {
                if (outcome.Status == TemporalAgentTurnOutcomeStatus.Unavailable)
                    Console.Error.WriteLine("No retained outcome; reconcile authoritative state. Do not resubmit.");
                else
                    Console.WriteLine($"{outcome.Status}; tool counts: {outcome.ToolCallCounts}");
                return;
            }
            if (attempt < 5)
            {
                await Task.Delay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
        throw new TimeoutException("Still pending after six queries; retain both IDs for later recovery.");
        // END SNIPPET docs/how-to/MAF/usage.md#request-id-recovery
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
