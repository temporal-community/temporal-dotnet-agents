using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Integration tests for MAINT-1: verifies that <c>_settingsResolved</c> and
/// <c>_resolvedMaxToolCallsPerTurn</c> are correctly cached on <see cref="TemporalAIAgent"/>
/// across multiple turns so that (a) the resolution handshake only fires on the first step of
/// the first turn, and (b) subsequent turns use the resolved cap rather than the hard-coded
/// default of 20.
/// </summary>
[Trait("Category", "Integration")]
public class Maint1SettingsResolutionTests
{
    private const string RunDurableAgentStepActivity = "TemporalCommunity.Extensions.Agents.RunDurableAgentStep";

    /// <summary>
    /// Verifies that <c>_resolvedMaxToolCallsPerTurn</c> from turn 1 is used on turn 2.
    /// The agent is registered with <c>MaxToolCallsPerTurn = 2</c>. Turn 1 resolves and
    /// caches this value. Turn 2 scripts an infinite-loop tool (always returns a tool call);
    /// the loop must cap at 2 — not the hard-coded default of 20. If the cache is broken
    /// and the hard-coded 20 resets, the scripted client would run out of responses and the
    /// test would fail before reaching the assertion.
    /// </summary>
    [Fact]
    public async Task SubAgent_SecondTurn_UsesResolvedMaxToolCallsPerTurn()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int resolvedCap = 2;

        // Turn 1: scripted final answer (no tool calls — resolves settings, cap = resolvedCap).
        // Turn 2: resolvedCap tool-call responses + 1 more to verify we do NOT go to 21st.
        // The scripted client throws on exhaustion, so scripting exactly (resolvedCap + 1) LLM
        // responses for turn 2 is sufficient: if the cap is honoured the last response is not
        // consumed; if the bug resets to 20 the client throws at entry resolvedCap+2.
        var loopTool = new FunctionCallContent("call-loop", "loop_tool",
            new Dictionary<string, object?> { ["input"] = "go" });
        var scripted = new ScriptedChatClient(
        [
            // Turn 1: single final answer.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn1Done")),
            // Turn 2: resolvedCap loop iterations (all tool calls), then a final.
            // The cap should stop the loop after resolvedCap tool dispatches so the
            // final-answer response below is never dequeued (the "iteration cap" message fires).
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
            // If the cache is broken and maxIterations resets to 20, iteration 3 would dequeue
            // this entry and the cap assertion below would fail.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ShouldNotBeReached")),
        ]);

        var taskQueue = $"maint1-resolved-cap-{Guid.NewGuid():N}";
        using var host = BuildHost<TwoTurnSubAgentWorkflow>(
            env.Client, taskQueue, scripted,
            configureAgent: agent =>
            {
                agent.MaxToolCallsPerTurn = resolvedCap;
                agent.AddTool(AIFunctionFactory.Create(() => "looped", "loop_tool"));
            });
        await host.StartAsync();

        try
        {
            var wfId = $"maint1-resolved-cap-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (TwoTurnSubAgentWorkflow wf) => wf.RunAsync("Turn1Msg", "Turn2Msg"),
                new WorkflowOptions(wfId, taskQueue));

            var results = await handle.GetResultAsync();

            // Turn 1: scripted final answer.
            Assert.Equal("Turn1Done", results[0]);

            // Turn 2: the MAF pipeline (TemporalAIAgent) does not emit a sentinel text string
            // when the iteration cap fires — it returns whatever messages accumulated (last entry
            // is a tool-result ChatMessage whose .Text is null/empty). Cap enforcement is
            // verified by the CallCount assertion below, not by response text.
            Assert.Equal("", results[1]);

            // Confirm the scripted client made exactly 1 (turn1) + resolvedCap (turn2 iterations)
            // calls in total. If the cap resets to 20, the client would throw at call resolvedCap+2
            // (running out of scripted responses before the assertion is reached).
            Assert.Equal(1 + resolvedCap, scripted.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Verifies that <c>NeedsWorkerSettingsResolution</c> is <c>true</c> only for the first
    /// <c>RunDurableAgentStep</c> activity across a two-turn sub-agent execution. The total
    /// number of <c>RunDurableAgentStep</c> activities fired is asserted to be exactly 3:
    /// one for turn 1 (final answer) and two for turn 2 (cap = 2 tool iterations). A proxy
    /// that fires resolution on every turn-start would produce more activities for turn 2
    /// because the first step of each turn would re-run the resolution handshake.
    /// </summary>
    [Fact]
    public async Task SubAgent_MultiTurn_RunDurableAgentStepCountMatchesExpected()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int cap = 2;
        var loopTool = new FunctionCallContent("call-1", "count_tool",
            new Dictionary<string, object?> { ["input"] = "x" });
        var scripted = new ScriptedChatClient(
        [
            // Turn 1: immediate final answer — 1 step.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done1")),
            // Turn 2: cap iterations of tool calls — cap steps.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
        ]);

        var taskQueue = $"maint1-step-count-{Guid.NewGuid():N}";
        using var host = BuildHost<TwoTurnSubAgentWorkflow>(
            env.Client, taskQueue, scripted,
            configureAgent: agent =>
            {
                agent.MaxToolCallsPerTurn = cap;
                agent.AddTool(AIFunctionFactory.Create(() => "counted", "count_tool"));
            });
        await host.StartAsync();

        try
        {
            var wfId = $"maint1-step-count-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (TwoTurnSubAgentWorkflow wf) => wf.RunAsync("Hello", "World"),
                new WorkflowOptions(wfId, taskQueue));

            await handle.GetResultAsync();

            // Collect the number of RunDurableAgentStep activities from the workflow history.
            var stepCount = await CountActivitiesAsync(handle, RunDurableAgentStepActivity);

            // Turn 1: 1 step (final answer).
            // Turn 2: cap = 2 steps (2 tool-call iterations before cap fires).
            // Total expected: 1 + cap = 3.
            Assert.Equal(1 + cap, stepCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<int> CountActivitiesAsync(WorkflowHandle handle, string activityTypeName)
    {
        var count = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a
                && a.ActivityType.Name == activityTypeName)
            {
                count++;
            }
        }
        return count;
    }

    private static IHost BuildHost<TWorkflow>(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scripted,
        Action<DurableAgentBuilder>? configureAgent)
        where TWorkflow : class
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<TWorkflow>()
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureAgent?.Invoke(agent);
                });
            });

        return builder.Build();
    }

    // ── Orchestrating workflow ───────────────────────────────────────────────────

    [Workflow("Maint1Tests.TwoTurnSubAgent")]
    internal class TwoTurnSubAgentWorkflow
    {
        [WorkflowRun]
        public async Task<string[]> RunAsync(string turn1Msg, string turn2Msg)
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);

            var r1 = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, turn1Msg)],
                session).ConfigureAwait(true);

            var r2 = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, turn2Msg)],
                session).ConfigureAwait(true);

            // Return the last message text from each turn.
            var t1Text = r1.Messages.Count > 0 ? r1.Messages[^1].Text ?? "" : "";
            var t2Text = r2.Messages.Count > 0 ? r2.Messages[^1].Text ?? "" : "";
            return [t1Text, t2Text];
        }
    }
}
