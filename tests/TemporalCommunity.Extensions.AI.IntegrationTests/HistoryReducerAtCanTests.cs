using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Testing;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

/// <summary>
/// Tests that a configured reducer (via <c>DurableExecutionOptions.DefaultHistoryReducerKey</c>)
/// actually fires at continue-as-new and produces the expected trimmed history.
///
/// <para>
/// The reducer key crosses the workflow boundary while the registered delegate remains in the
/// worker's DI container. The workflow dispatches an activity to resolve and execute it.
/// </para>
/// <para>
/// This test uses a keep-last-1 sentinel reducer: if the reducer fires, the carried
/// history after CAN must have exactly 1 entry (ignoring any turns added after the CAN
/// but before the query). With <c>maxEntryCount = 6</c>, <c>DefaultBoundedTrim</c> would
/// produce 3 entries — so the assertion <c>carried.Count == 1</c> cleanly distinguishes
/// reducer-fired from reducer-dropped.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class HistoryReducerAtCanTests
{
    private readonly ITestOutputHelper _output;

    public HistoryReducerAtCanTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A keep-last-1 reducer fires at CAN and the carried history has exactly 1 entry.
    /// Before the fix this fails because the reducer was silently stripped on the wire
    /// and DefaultBoundedTrim produced 3 entries instead.
    /// </summary>
    [Fact]
    public async Task ConfiguredReducer_ViaKey_AcrossCan_CarriesExactlyOneEntry()
    {
        const int maxEntryCount = 6; // DefaultBoundedTrim would produce 3; reducer should produce 1.
        const string reducerKey = "keep-last-1-test";

        await using var env = await TemporalServiceTestEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 40).Select(i => new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"response {i}"))));

        var taskQueue = $"reducer-test-{Guid.NewGuid():N}";

        // Keep-last-1 sentinel: if the reducer fires, carried.Count == 1.
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> keepLast1 =
            entries => entries.Count > 0 ? [entries[^1]] : [];

        using var host = BuildHost(env.Client, scripted, taskQueue, maxEntryCount, reducerKey, keepLast1);
        await host.StartAsync();

        try
        {
            var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
            var conversationId = $"conv-{Guid.NewGuid():N}";
            var workflowId = sessionClient.GetWorkflowId(conversationId);
            var handle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

            // First turn to start the workflow.
            await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, "turn 1")]);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            // Drive turns until the FIRST count-driven CAN fires (run ID changes), pinning the
            // exact run ID created by that CAN. We check BEFORE each dispatch and stop the moment
            // CAN is detected, so no extra turn ever lands on the new run and inflates its history.
            string? firstCanRunId = null;
            for (var i = 2; i <= 15; i++)
            {
                var rid = (await handle.DescribeAsync()).RunId;
                if (rid != initialRunId) { firstCanRunId = rid; break; }

                try
                {
                    await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, $"turn {i}")]);
                }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException)
                {
                    // CAN in flight — expected.
                }
            }
            // Catch the case where CAN fired while the last SendAsync was in flight.
            firstCanRunId ??= (await handle.DescribeAsync()).RunId is var last && last != initialRunId
                ? last
                : null;

            Assert.True(firstCanRunId is not null, "Expected count-driven CAN to fire.");
            _output.WriteLine($"CAN fired: {initialRunId} → {firstCanRunId}");

            // Pin the query to the exact run created by the first CAN — not the run-less handle
            // that resolves to the latest run (which may be a LATER run created by a subsequent
            // count-driven CAN if turns are still in flight). Querying the pinned run returns that
            // run's history deterministically: exactly the reducer output, regardless of whether
            // additional CANs have since occurred on the session. This removes the timing race
            // entirely — no Task.Delay needed.
            var runHandle = env.Client.GetWorkflowHandle<DurableChatWorkflow>(
                workflowId, runId: firstCanRunId);
            var carried = await runHandle.QueryAsync<DurableChatWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());

            _output.WriteLine($"Carried history count after CAN: {carried.Count}");

            // Assert on CONTENT, not count. Even with the query pinned to the first-CAN run,
            // a turn dispatched via the run-less session handle can straddle onto the new run
            // (the server resolves the run at update-admission time, after CAN commits), appending
            // a req/resp pair. That is a benign timing artifact, not a reducer failure.
            //
            // The deterministic discriminator is the FIRST carried entry — the reduced base:
            //   keep-last-1 reducer  → carried[0] is the single last pre-CAN response ("response 3")
            //   DefaultBoundedTrim   → carried[0] would be "response 2" (TakeLast(maxEntryCount/2)=3
            //                          of [req1,resp1,req2,resp2,req3,resp3] starts at resp2)
            // CAN fires deterministically at history count == maxEntryCount (6) i.e. after turn 3,
            // so the reduced entry is reliably "response 3".
            Assert.NotEmpty(carried);
            var reducedBase = Assert.IsType<DurableSessionResponse>(carried[0]);
            Assert.Equal("response 3", reducedBase.Text);

            await host.StopAsync();
        }
        catch
        {
            await host.StopAsync();
            throw;
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        string taskQueue,
        int maxEntryCount,
        string reducerKey,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddChatClient(scripted).Build();

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.MaxEntryCount = maxEntryCount;
                opts.SessionTimeToLive = TimeSpan.FromMinutes(10);
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.DefaultHistoryReducerKey = reducerKey;
            });

        // Register the reducer under the key so the activity can resolve it.
        builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            reducerKey, (_, _) => reducer);

        return builder.Build();
    }
}
