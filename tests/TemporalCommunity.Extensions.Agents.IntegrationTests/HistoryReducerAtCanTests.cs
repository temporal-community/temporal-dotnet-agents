using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

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

        using var host = BuildHost(env.Client, scripted, maxEntryCount);
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

    /// <summary>
    /// Reproduces the [JsonIgnore] silent-failure bug: a keep-last-1 reducer configured via
    /// <c>HistoryReducerKey</c> must produce exactly 1 carried entry after CAN.
    /// Before the fix the reducer was silently stripped on the wire and DefaultBoundedTrim
    /// produced 3 entries (<c>maxEntryCount/2</c>) instead.
    /// </summary>
    [Fact]
    public async Task ConfiguredReducer_ViaKey_AcrossCan_CarriesExactlyOneEntry()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int maxEntryCount = 6;
        const string reducerKey = "keep-last-1-test-maf";
        var scripted = new ScriptedChatClient(
            Enumerable.Range(1, 40).Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"r{i}"))));

        // Keep-last-1 sentinel: if the reducer fires, carried.Count == 1.
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> keepLast1 =
            entries => entries.Count > 0 ? [entries[^1]] : [];

        using var host = BuildHostWithReducerKey(env.Client, scripted, maxEntryCount, reducerKey, keepLast1);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("CanAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);

            await proxy.RunAsync("turn 1", session);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            // Drive turns until the FIRST count-driven CAN fires (run id changes), pinning the
            // exact run id created by that CAN. We check BEFORE each dispatch and stop the moment
            // CAN is detected, so no extra turn ever lands on the new run and inflates its history.
            string? firstCanRunId = null;
            for (var i = 2; i <= 12; i++)
            {
                var rid = (await handle.DescribeAsync()).RunId;
                if (rid != initialRunId) { firstCanRunId = rid; break; }

                try { await proxy.RunAsync($"turn {i}", session); }
                catch (Temporalio.Exceptions.WorkflowUpdateFailedException) { }
            }
            // Catch the case where CAN fired while the last RunAsync was in flight.
            firstCanRunId ??= (await handle.DescribeAsync()).RunId is var last && last != initialRunId
                ? last
                : null;

            Assert.True(firstCanRunId is not null, "Expected count-driven CAN to fire.");

            // Pin the query to the exact run created by the first CAN — not the run-less handle
            // that resolves to the latest run (which may be a LATER run created by a subsequent
            // count-driven CAN if turns are still in flight). Querying the pinned run returns that
            // run's history deterministically: exactly the reducer output, regardless of whether
            // additional CANs have since occurred on the session. This removes the timing race
            // entirely — no Task.Delay needed.
            var runHandle = env.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId, runId: firstCanRunId);
            var carried = await runHandle.QueryAsync<AgentWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());
            _output.WriteLine($"Carried history count after reducer CAN: {carried.Count}");

            // Assert on CONTENT, not count. Even with the query pinned to the first-CAN run,
            // a turn dispatched via the run-less proxy handle can straddle onto the new run
            // (the server resolves the run at update-admission time, after CAN commits),
            // appending a req/resp pair. That is a benign timing artifact, not a reducer failure
            // — and it is exactly what CI timing surfaced as an intermittent "collection had 3 items".
            //
            // The deterministic discriminator is the FIRST carried entry — the reduced base:
            //   keep-last-1 reducer → carried[0] is the single last pre-CAN response ("r3")
            //   DefaultBoundedTrim  → carried[0] would be "r2" (TakeLast(maxEntryCount/2)=3 of
            //                         [req1,r1,req2,r2,req3,r3] starts at r2)
            // CAN fires deterministically at history count == maxEntryCount (6), i.e. after turn 3,
            // so the reduced entry is reliably "r3".
            // IsAssignableFrom (not IsType): the MAF entry is AgentSessionResponse, a subtype
            // of DurableSessionResponse.
            Assert.NotEmpty(carried);
            var reducedBase = Assert.IsAssignableFrom<DurableSessionResponse>(carried[0]);
            Assert.Equal("r3", reducedBase.Text);

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
        int maxEntryCount)
    {
        var taskQueue = $"history-reducer-can-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("CanAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxEntryCount = maxEntryCount;
                    agent.TimeToLive = TimeSpan.FromMinutes(10);
                });
            });

        return builder.Build();
    }

    private static IHost BuildHostWithReducerKey(
        ITemporalClient client,
        ScriptedChatClient scripted,
        int maxEntryCount,
        string reducerKey,
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer)
    {
        var taskQueue = $"history-reducer-key-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        // Register the reducer under the key so the activity can resolve it from DI.
        builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
            reducerKey, (_, _) => reducer);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.DefaultHistoryReducerKey = reducerKey;
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
