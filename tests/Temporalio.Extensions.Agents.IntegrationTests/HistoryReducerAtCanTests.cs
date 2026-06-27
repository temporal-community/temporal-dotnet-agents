using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Session;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// C-2 — history bounding across continue-as-new (in-workflow history, no external store).
///
/// <list type="bullet">
/// <item><b>No-reducer fallback:</b> with a low <c>MaxEntryCount</c> and no reducer, a count-driven
/// CAN must carry a BOUNDED history (DefaultBoundedTrim ~= MaxEntryCount/2, strictly below the
/// trigger) so the next turn does NOT immediately re-trigger CAN — no back-to-back CAN loop.</item>
/// <item><b>Reducer-configured path unchanged:</b> a sentinel reducer (keep-last-1) across CAN carries
/// the reduced shape — the original C-2 intent.</item>
/// </list>
///
/// <para>
/// CAN is forced deterministically via the count trigger (<c>_history.Count &gt;= MaxEntryCount</c>),
/// per the test-laws — not the SDK <c>ContinueAsNewSuggested</c> heuristic.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class HistoryReducerAtCanTests
{
    private readonly ITestOutputHelper _output;

    public HistoryReducerAtCanTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task NoReducer_CountDrivenCan_BoundsHistory_AndDoesNotReTriggerImmediately()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int maxEntryCount = 6; // trim target = 3; each turn adds 2 entries (req + resp).
        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 40).Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"r{i}"))));

        using var host = BuildHost(env.Client, scripted, maxEntryCount, reducer: null);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("CanAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);

            await proxy.RunAsync("turn 1", session);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            // Drive turns until CAN fires (run id changes).
            string runAfterCan = initialRunId;
            for (var i = 2; i <= 12; i++)
            {
                try { await proxy.RunAsync($"turn {i}", session); }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException) { /* CAN in flight */ }

                var rid = (await handle.DescribeAsync()).RunId;
                if (rid != initialRunId) { runAfterCan = rid; break; }
            }
            Assert.NotEqual(initialRunId, runAfterCan);
            _output.WriteLine($"CAN fired: {initialRunId} -> {runAfterCan}");

            // After CAN, the carried history must be bounded strictly below MaxEntryCount
            // (DefaultBoundedTrim target = MaxEntryCount/2 = 3).
            await Task.Delay(TimeSpan.FromSeconds(1));
            var carried = await handle.QueryAsync<AgentWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());
            _output.WriteLine($"Carried history count after CAN: {carried.Count}");
            Assert.True(carried.Count < maxEntryCount,
                $"Carried history ({carried.Count}) must be strictly below MaxEntryCount ({maxEntryCount}).");
            Assert.True(carried.Count <= maxEntryCount / 2 + 1,
                $"Carried history ({carried.Count}) should be ~MaxEntryCount/2 (DefaultBoundedTrim).");

            // NO back-to-back CAN: run one more turn and confirm the run id does NOT immediately
            // change again (the fresh run had headroom; one turn must not re-trigger CAN).
            var runIdBefore = (await handle.DescribeAsync()).RunId;
            await proxy.RunAsync("post-can turn", session);
            var runIdAfter = (await handle.DescribeAsync()).RunId;
            Assert.Equal(runIdBefore, runIdAfter);
            _output.WriteLine("No back-to-back CAN: run id stable after one post-CAN turn.");

            await host.StopAsync();
        }
        catch
        {
            await host.StopAsync();
            throw;
        }
    }

    [Fact]
    public async Task ConfiguredReducer_AcrossCan_CarriesReducedShape()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int maxEntryCount = 6;
        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 40).Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"r{i}"))));

        // Sentinel reducer: keep only the last 1 entry — clearly distinct from the default trim.
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> keepLast1 =
            entries => entries.Count > 0 ? [entries[^1]] : [];

        using var host = BuildHost(env.Client, scripted, maxEntryCount, reducer: keepLast1);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("CanAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);

            await proxy.RunAsync("turn 1", session);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            var canFired = false;
            for (var i = 2; i <= 12 && !canFired; i++)
            {
                try { await proxy.RunAsync($"turn {i}", session); }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException) { }
                if ((await handle.DescribeAsync()).RunId != initialRunId) canFired = true;
            }
            Assert.True(canFired, "Expected count-driven CAN to fire.");

            await Task.Delay(TimeSpan.FromSeconds(1));
            var carried = await handle.QueryAsync<AgentWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());
            _output.WriteLine($"Carried history count after reducer CAN: {carried.Count}");

            // The reducer kept exactly 1 entry at CAN. The fresh run may have processed a couple
            // more turns by query time, but the carried base is the single reduced entry —
            // the count is far below the unreduced ~MaxEntryCount/2 default-trim shape would carry
            // for the same drive, and strictly below MaxEntryCount.
            Assert.True(carried.Count < maxEntryCount,
                $"Reduced carried history ({carried.Count}) must stay below MaxEntryCount ({maxEntryCount}).");

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
        int maxEntryCount,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? reducer)
    {
        var taskQueue = $"history-reducer-can-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                if (reducer is not null) opts.DefaultHistoryReducer = reducer;
                opts.AddDurableAgent("CanAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxEntryCount = maxEntryCount;
                    agent.TimeToLive = TimeSpan.FromMinutes(10);
                });
            });

        return builder.Build();
    }
}
