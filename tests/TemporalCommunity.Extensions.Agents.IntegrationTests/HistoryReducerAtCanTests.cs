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
/// <item><b>No-reducer recovery:</b> with a low <c>MaxEntryCount</c> and no reducer, a count-driven
/// boundary blocks without trimming. An explicit recovery trim carries a bounded history and the
/// next turn does NOT immediately re-trigger CAN.</item>
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
    public async Task NoReducer_BlocksUntilExplicitTrim_ThenContinuesWithoutImmediateRetrigger()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int maxEntryCount = 6;
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

            await proxy.RunAsync("turn 2", session);
            await proxy.RunAsync("turn 3", session);
            var budget = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .GetHistoryBudgetAsync(session.SessionId);
            Assert.True(budget.IsHistoryBlocked);
            Assert.Equal(6, budget.RetainedHistoryEntryCount);

            var originalRunId = (await handle.DescribeAsync()).RunId;
            var recovery = await host.Services.GetRequiredService<ITemporalAgentClient>()
                .TrimBlockedHistoryAsync(session.SessionId, "trim-history-1");
            Assert.False(recovery.IsBlocked);
            Assert.Equal("trim-succeeded", recovery.Outcome);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline
                && (await handle.DescribeAsync()).RunId == originalRunId)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            Assert.NotEqual(originalRunId, (await handle.DescribeAsync()).RunId);

            // After explicit trim, carried history is below the trigger and includes the
            // marker that records the intentionally removed range.
            var carried = await handle.QueryAsync<AgentWorkflow, IReadOnlyList<DurableSessionEntry>>(
                wf => wf.GetHistory());
            _output.WriteLine($"Carried history count after CAN: {carried.Count}");
            Assert.True(carried.Count < maxEntryCount,
                $"Carried history ({carried.Count}) must be strictly below MaxEntryCount ({maxEntryCount}).");
            Assert.Contains(carried, entry => entry is TruncationMarkerEntry);

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
    /// Reproduces the [JsonIgnore] silent-failure bug: a keep-last-turn reducer configured via
    /// <c>HistoryReducerKey</c> must preserve a complete request/response turn after CAN.
    /// Before the fix the reducer was silently stripped on the wire and an explicit trim
    /// projection would have produced the final completed turn instead.
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

        // Keep-last-turn sentinel: projections must preserve complete request/response pairs.
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> keepLastTurn =
            entries => entries.TakeLast(2).ToList();

        using var host = BuildHostWithReducerKey(env.Client, scripted, maxEntryCount, reducerKey, keepLastTurn);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("CanAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);

            await proxy.RunAsync("turn 1", session);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            await proxy.RunAsync("turn 2", session);
            await proxy.RunAsync("turn 3", session);

            // Do not submit extra turns while the reducer is running: admitted turns
            // legitimately become part of its final snapshot.
            string? firstCanRunId = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var rid = (await handle.DescribeAsync()).RunId;
                if (rid != initialRunId) { firstCanRunId = rid; break; }
                await Task.Delay(TimeSpan.FromMilliseconds(75));
            }

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
            // The deterministic discriminator is the reduced complete turn:
            //   keep-last-turn reducer → carried contains the request and response for "r3"
            //   Explicit trim → carried[0] would be the final retained completed turn (the raw
            //                         TakeLast(maxEntryCount/2)=3 suffix is narrowed at its pair boundary)
            //                         [req1,r1,req2,r2,req3,r3] starts at req2)
            // CAN fires deterministically at history count == maxEntryCount (6), i.e. after turn 3,
            // so the reduced entry is reliably "r3".
            // IsAssignableFrom (not IsType): the MAF entry is AgentSessionResponse, a subtype
            // of DurableSessionResponse.
            Assert.Collection(
                carried,
                entry => Assert.IsAssignableFrom<DurableSessionRequest>(entry),
                entry => Assert.Equal("r3", Assert.IsAssignableFrom<DurableSessionResponse>(entry).Text));

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
