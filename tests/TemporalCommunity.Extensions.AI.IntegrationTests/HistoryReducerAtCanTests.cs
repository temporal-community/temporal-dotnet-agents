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
/// Tests that a configured <c>HistoryReducer</c> (via <c>DurableExecutionOptions.HistoryReducerKey</c>)
/// actually fires at continue-as-new and produces the expected trimmed history.
///
/// <para>
/// Before the fix: <see cref="DurableChatWorkflowInput.HistoryReducer"/> was <c>[JsonIgnore]</c>
/// and stripped on the wire, so the reducer never fired — <c>DefaultBoundedTrim</c>
/// silently took over. The existing MAF test asserted <c>carried.Count &lt; maxEntryCount</c>,
/// which the fallback trim also satisfies, so it could not detect the bug.
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

        await using var env = await WorkflowEnvironment.StartLocalAsync();
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

            // Drive turns until CAN fires (run ID changes). maxEntryCount=6 → CAN after 3 turns
            // (each adds 2 entries: request + response).
            var canFired = false;
            for (var i = 2; i <= 15 && !canFired; i++)
            {
                try
                {
                    await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, $"turn {i}")]);
                }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException)
                {
                    // CAN in flight — expected.
                }

                var rid = (await handle.DescribeAsync()).RunId;
                if (rid != initialRunId)
                {
                    canFired = true;
                    _output.WriteLine($"CAN fired: {initialRunId} → {rid}");
                }
            }

            Assert.True(canFired, "Expected count-driven CAN to fire.");

            // Allow the new run to stabilize.
            await Task.Delay(TimeSpan.FromSeconds(2));

            var carried = await handle.QueryAsync<DurableChatWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());

            _output.WriteLine($"Carried history count after CAN: {carried.Count}");

            // If the reducer fired: exactly 1 entry (keep-last-1).
            // If the reducer was silently dropped: DefaultBoundedTrim would produce 3 entries.
            // The assertion below distinguishes reducer-fired from reducer-dropped.
            Assert.Single(carried);

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
                opts.HistoryReducer = reducer; // still set for local/test use; key used for durable path
            });

        // Register the reducer under the key so the activity can resolve it.
        builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            reducerKey, (_, _) => reducer);

        return builder.Build();
    }
}
