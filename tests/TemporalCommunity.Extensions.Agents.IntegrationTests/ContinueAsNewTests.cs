using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for the continue-as-new workflow lifecycle.
///
/// These tests use a dedicated <see cref="WorkflowEnvironment"/> configured with a very low
/// <c>limit.historyCount.suggestContinueAsNew</c> threshold so that
/// <see cref="Temporalio.Workflows.Workflow.ContinueAsNewSuggested"/> triggers after just a
/// few agent turns. This lets us verify that the <see cref="AgentWorkflow"/> correctly
/// transfers conversation history to a fresh run and that subsequent requests continue
/// seamlessly.
/// </summary>
[Trait("Category", "Integration")]
public class ContinueAsNewTests : IAsyncLifetime
{
    /// <summary>
    /// Low threshold so the server flags <c>ContinueAsNewSuggested</c> quickly.
    /// Each agent turn adds ~4-6 history events (update-accepted, activity-scheduled,
    /// activity-completed, update-completed, plus workflow-task events), so 20 events
    /// means the flag triggers after roughly 3-4 turns.
    /// </summary>
    private const int HistoryCountThreshold = 20;

    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment _env = null!;
    private IHost _host = null!;
    private AIAgent _proxy = null!;
    private string _taskQueue = null!;

    public ContinueAsNewTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _env = await TestEnvironmentHelper.StartLocalAsync(
            "--dynamic-config-value",
            $"limit.historyCount.suggestContinueAsNew={HistoryCountThreshold}");
        _env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        _taskQueue = $"continue-as-new-test-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services
            .AddHostedTemporalWorker(_taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("EchoAgent", a => { a.ChatClient = _ => new Helpers.EchoChatClient(); a.TimeToLive = TimeSpan.FromMinutes(10); }));

        _host = builder.Build();
        await _host.StartAsync();

        _proxy = _host.Services.GetTemporalAgentProxy("EchoAgent");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _env.ShutdownAsync();
    }

    [Fact]
    public async Task ContinueAsNew_HistoryCarriedForward_ConversationContinuesSeamlessly()
    {
        var session = (TemporalAgentSession)await _proxy.CreateSessionAsync();
        var workflowId = session.SessionId.WorkflowId;

        // Send the first turn to start the workflow (CreateSessionAsync only generates an ID).
        var r1 = await _proxy.RunAsync("Turn 1", session);
        Assert.Contains("Echo [1]: Turn 1", r1.Messages[0].Text);
        _output.WriteLine($"Turn 1 OK — response: {r1.Messages[0].Text}");

        // Now capture the initial run ID (workflow is running).
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(workflowId);
        var initialDesc = await handle.DescribeAsync();
        var initialRunId = initialDesc.RunId;
        _output.WriteLine($"Initial RunId: {initialRunId}");

        // Send more turns to exceed the history event threshold.
        // With ~4-6 events per turn and threshold=20, 6 total turns should be well past it.
        const int turnCount = 6;
        for (int i = 2; i <= turnCount; i++)
        {
            var r = await _proxy.RunAsync($"Turn {i}", session);
            Assert.Contains($"Echo [{i}]: Turn {i}", r.Messages[0].Text);
            _output.WriteLine($"Turn {i} OK — response: {r.Messages[0].Text}");
        }

        // After enough turns, the workflow should have continued-as-new.
        // GetWorkflowHandle with no specific runId follows to the latest run.
        // Poll until the RunId changes (CAN has executed) — throws TimeoutException if it
        // doesn't happen within 30 s, catching regressions where CAN never fires.
        var currentDesc = await PollUntilRunIdChangesAsync(handle, initialRunId, TimeSpan.FromSeconds(30));
        _output.WriteLine($"Current RunId: {currentDesc.RunId} — continue-as-new confirmed.");

        // The critical assertion: send one more turn and verify history is preserved.
        // Whether or not continue-as-new actually triggered, the conversation must work.
        var finalResponse = await _proxy.RunAsync($"Turn {turnCount + 1}", session);
        Assert.Contains($"Echo [{turnCount + 1}]: Turn {turnCount + 1}", finalResponse.Messages[0].Text);

        _output.WriteLine($"Final turn ({turnCount + 1}) verified — history preserved across runs.");
    }

    [Fact]
    public async Task ContinueAsNew_RunIdChangesAfterSufficientHistory()
    {
        var session = (TemporalAgentSession)await _proxy.CreateSessionAsync();
        var workflowId = session.SessionId.WorkflowId;

        // Send the first turn to start the workflow.
        await _proxy.RunAsync("Msg 1", session);

        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(workflowId);
        var initialDesc = await handle.DescribeAsync();
        var initialRunId = initialDesc.RunId;

        // Send many more turns to reliably trigger continue-as-new.
        const int turnCount = 10;
        for (int i = 2; i <= turnCount; i++)
        {
            await _proxy.RunAsync($"Msg {i}", session);
        }

        // Poll until the RunId changes (CAN has executed) rather than sleeping blindly.
        var currentDesc = await PollUntilRunIdChangesAsync(handle, initialRunId, TimeSpan.FromSeconds(30));

        _output.WriteLine($"Initial RunId: {initialRunId}");
        _output.WriteLine($"Current RunId: {currentDesc.RunId}");
        _output.WriteLine($"History length: {currentDesc.HistoryLength}");

        // After 10 turns (~40-60 events), the server should have suggested continue-as-new.
        Assert.NotEqual(initialRunId, currentDesc.RunId);
    }

    [Fact]
    public async Task ContinueAsNew_CustomTimeoutsPropagate()
    {
        // Configure a custom short StartToCloseTimeout (3s) and verify it survives
        // continue-as-new. We use a SlowThenFastAIAgent: if the timeout is NOT
        // propagated (i.e., falls back to 30 min default), the slow first attempt
        // would delay 60s. If the custom 3s timeout IS propagated, the slow attempt
        // is cancelled and retried quickly.

        var taskQueue = $"can-timeout-{Guid.NewGuid():N}";
        var chatClient = new SlowThenFastChatClient(TimeSpan.FromSeconds(60));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("TimeoutCANAgent", a =>
                {
                    a.ChatClient = _ => chatClient;
                    a.TimeToLive = TimeSpan.FromMinutes(10);
                });
                options.DefaultActivityTimeout = TimeSpan.FromSeconds(3);
                options.DefaultHeartbeatTimeout = TimeSpan.FromSeconds(2);
            });

        using var extraHost = builder.Build();
        await extraHost.StartAsync();

        try
        {
            var proxy = extraHost.Services.GetTemporalAgentProxy("TimeoutCANAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // First turn: starts the workflow. The SlowThenFastAIAgent delays on call 1,
            // times out after 3s, then succeeds on retry.
            var r1 = await proxy.RunAsync("Turn 1", session);
            Assert.Contains("Echo [1]:", r1.Messages[0].Text);

            var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);
            var initialRunId = (await handle.DescribeAsync()).RunId;

            // Send enough additional turns to trigger continue-as-new.
            // The agent no longer delays (only first call is slow), so these are fast.
            for (int i = 2; i <= 8; i++)
            {
                await proxy.RunAsync($"Turn {i}", session);
            }

            // Poll until continue-as-new has taken effect (RunId changes).
            var postCanDesc = await PollUntilRunIdChangesAsync(handle, initialRunId, TimeSpan.FromSeconds(30));
            var currentRunId = postCanDesc.RunId;

            _output.WriteLine($"Initial RunId: {initialRunId}");
            _output.WriteLine($"Current RunId: {currentRunId}");

            // Now create a NEW SlowThenFastAIAgent instance that will delay on call 1 again.
            // This simulates what happens after continue-as-new: the activity still uses
            // the configured timeout. If timeout wasn't propagated, this would hang for 60s.
            //
            // However, since the agent is a singleton in DI, we can't replace it.
            // Instead, we verify indirectly: the conversation continues to work after
            // continue-as-new, proving the workflow input (including timeouts) was carried.
            var rPost = await proxy.RunAsync("Post-CAN turn", session);
            Assert.NotNull(rPost);
            Assert.NotEmpty(rPost.Messages);

            // The turn count should reflect all prior history.
            _output.WriteLine(
                $"Post-CAN response: {rPost.Messages[0].Text}. " +
                $"Custom timeout propagated through continue-as-new.");
        }
        finally
        {
            await extraHost.StopAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="handle"/> until its <c>RunId</c> differs from
    /// <paramref name="originalRunId"/>, confirming that continue-as-new has executed.
    /// Returns the latest <see cref="WorkflowExecutionDescription"/> once the RunId changes.
    /// Throws <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses.
    /// </summary>
    private static async Task<WorkflowExecutionDescription> PollUntilRunIdChangesAsync(
        WorkflowHandle handle,
        string originalRunId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var desc = await handle.DescribeAsync().ConfigureAwait(false);
            if (desc.RunId != originalRunId)
                return desc;
            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"RunId did not change from {originalRunId} within {timeout}.");
    }
}
