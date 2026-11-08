using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Exceptions;

internal static class TurnRecoveryExample
{
    internal sealed record Receipt(string SessionWorkflowId, string RequestId);

    internal static async Task DispatchAsync(IServiceProvider services, string receiptPath)
    {
        if (File.Exists(receiptPath))
        {
            WriteReceiptAlreadyExists(receiptPath);
            return;
        }

        var proxy = services.GetRequiredKeyedService<AIAgent>("Assistant");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var receipt = new Receipt(session.SessionId.WorkflowId, Guid.NewGuid().ToString("N"));
        try
        {
            await using var file = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, receipt);
            file.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(receiptPath))
        {
            WriteReceiptAlreadyExists(receiptPath);
            return;
        }

        // Cancel only the RPC wait to demonstrate an ambiguous dispatch, not lost transport completion.
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            var response = await proxy.RunAsync(
                "What is the capital of France?",
                session,
                new TemporalAgentRunOptions { RequestId = receipt.RequestId },
                wait.Token);
            Console.WriteLine($"Dispatch completed: {response.Text}");
        }
        catch (WorkflowUpdateRpcTimeoutOrCanceledException exception)
        {
            Console.WriteLine($"Dispatch acknowledgement uncertain: {exception.Message}");
        }

        await RecoverAsync(services, receiptPath);
    }

    private static void WriteReceiptAlreadyExists(string receiptPath) =>
        Console.Error.WriteLine(
            $"Recovery receipt already exists at '{receiptPath}'. It was not overwritten. " +
            "Use --recover with this receipt after verifying it belongs to the intended turn, " +
            "or choose a new path.");

    internal static async Task RecoverAsync(IServiceProvider services, string receiptPath)
    {
        var receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(receiptPath))
            ?? throw new JsonException("The recovery receipt is missing.");
        var sessionId = TemporalAgentSessionId.Parse(receipt.SessionWorkflowId);
        var client = services.GetRequiredService<ITemporalAgentClient>();
        TemporalAgentTurnOutcome outcome;
        try
        {
            outcome = await PollAsync(client, sessionId, receipt.RequestId, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is RpcException or WorkflowQueryFailedException or WorkflowQueryRejectedException
                or OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine($"Outcome query did not establish a result: {exception.Message}");
            throw;
        }

        switch (outcome.Status)
        {
            case TemporalAgentTurnOutcomeStatus.Succeeded:
                Console.WriteLine($"Recovered success; details omitted: {outcome.DetailsOmitted}");
                break;
            case TemporalAgentTurnOutcomeStatus.Failed:
                Console.WriteLine(
                    $"Recovered failure: {outcome.FailureErrorType ?? "(not retained)"}; " +
                    $"tool counts: {outcome.ToolCallCounts}. Reconcile external effects.");
                break;
            case TemporalAgentTurnOutcomeStatus.Unavailable:
                Console.Error.WriteLine(
                    "Outcome unavailable; this does not prove the turn never ran. " +
                    "Reconcile authoritative state; no turn was resubmitted.");
                break;
            default:
                throw new InvalidOperationException($"Unexpected terminal outcome: {outcome.Status}");
        }
    }

    internal static async Task<TemporalAgentTurnOutcome> PollAsync(
        ITemporalAgentClient client,
        TemporalAgentSessionId sessionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var queryWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryWait.CancelAfter(TimeSpan.FromSeconds(5));
            var outcome = await client.GetTurnOutcomeAsync(sessionId, requestId, queryWait.Token);
            if (outcome.Status != TemporalAgentTurnOutcomeStatus.Pending)
            {
                return outcome;
            }
            if (attempt < 5)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
        throw new TimeoutException("The turn is still pending after six queries; keep both IDs for later recovery.");
    }
}
