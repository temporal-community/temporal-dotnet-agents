using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for workflow lifecycle management: shutdown updates, concurrent update
/// serialization, and worker restart / history replay.
/// </summary>
[Trait("Category", "Integration")]
public class WorkflowLifecycleTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public WorkflowLifecycleTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Shutdown update ────────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_Update_CompletesWorkflow()
    {
        // Establish a session with one turn so the workflow is running.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        var r1 = await _fixture.AgentProxy.RunAsync("Before shutdown", session);
        Assert.Contains("Echo [1]:", r1.Messages[0].Text);

        // Get a typed handle and submit the validated shutdown update.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());

        // Wait for the workflow to complete (should be near-instant after update).
        await handle.GetResultAsync();

        // Confirm the workflow is actually completed.
        var desc = await handle.DescribeAsync();
        Assert.Equal(WorkflowExecutionStatus.Completed, desc.Status);

        _output.WriteLine($"Workflow {session.SessionId.WorkflowId} completed after shutdown update.");
    }

    [Fact]
    public async Task Shutdown_ThenNewRequest_StartsFreshWorkflow()
    {
        // Establish a session with one turn.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        await _fixture.AgentProxy.RunAsync("Turn 1", session);

        // Shutdown the workflow.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        await handle.ExecuteUpdateAsync(wf => wf.RequestShutdownAsync());
        await handle.GetResultAsync();

        // Send another request to the SAME session ID.
        // With IdReusePolicy.AllowDuplicate, this starts a fresh workflow run.
        var r2 = await _fixture.AgentProxy.RunAsync("After restart", session);

        // The fresh workflow has no prior history — turn count resets to 1.
        Assert.Contains("Echo [1]: After restart", r2.Messages[0].Text);

        _output.WriteLine("Fresh workflow started after shutdown; turn count reset to 1.");
    }

    // ── Concurrent updates serialization ───────────────────────────────────────

    [Fact]
    public async Task ConcurrentUpdates_CompleteOrRejectBusyWithoutCorruptingTurnOrder()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();
        const int concurrentRequests = 5;

        // Launch multiple RunAsync calls in parallel to the same session.
        var tasks = Enumerable.Range(1, concurrentRequests)
            .Select(i => _fixture.AgentProxy.RunAsync($"Msg-{i}", session))
            .ToArray();

        var responses = new List<Microsoft.Agents.AI.AgentResponse>();
        var failures = new List<WorkflowUpdateFailedException>();
        foreach (var task in tasks)
        {
            try
            {
                responses.Add(await task);
            }
            catch (WorkflowUpdateFailedException exception)
            {
                failures.Add(exception);
            }
        }

        Assert.NotEmpty(responses);
        Assert.All(responses, response => Assert.NotEmpty(response.Messages));
        Assert.Equal(concurrentRequests, responses.Count + failures.Count);
        foreach (var failure in failures)
        {
            var applicationFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
            Assert.Equal("DurableTurnBusy", applicationFailure.ErrorType);
        }

        var turnCounts = responses
            .Select(response => response.Messages[0].Text)
            .Select(text =>
            {
                var bracket = text.IndexOf('[');
                var closeBracket = text.IndexOf(']');
                return int.Parse(text[(bracket + 1)..closeBracket]);
            })
            .OrderBy(count => count)
            .ToList();
        Assert.Equal(Enumerable.Range(1, responses.Count).ToList(), turnCounts);

        _output.WriteLine(
            $"{responses.Count} concurrent updates completed; {failures.Count} distinct requests were rejected as busy.");
    }

    [Fact]
    public async Task SequentialUpdates_TurnCountsAreSequential()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();
        const int requestCount = 5;
        var results = new List<Microsoft.Agents.AI.AgentResponse>(requestCount);
        for (var i = 1; i <= requestCount; i++)
        {
            results.Add(await _fixture.AgentProxy.RunAsync($"Msg-{i}", session));
        }

        var turnCounts = results
            .Select(r => r.Messages[0].Text)
            .Select(text =>
            {
                // Parse "Echo [N]: ..." → N
                var bracket = text.IndexOf('[');
                var closeBracket = text.IndexOf(']');
                return int.Parse(text[(bracket + 1)..closeBracket]);
            })
            .OrderBy(n => n)
            .ToList();

        _output.WriteLine($"Turn counts observed: [{string.Join(", ", turnCounts)}]");

        Assert.Equal(Enumerable.Range(1, requestCount).ToList(), turnCounts);
    }

    // ── Fire-and-forget vs synchronous race ────────────────────────────────────

    [Fact]
    public async Task UpdateWithStart_FirstTurnInitializesBeforeUpdateAndWorkflowRemainsHealthy()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        var first = await _fixture.AgentProxy.RunAsync("First update-with-start", session);
        var second = await _fixture.AgentProxy.RunAsync("Healthy follow-up", session);

        Assert.Contains("Echo [1]: First update-with-start", first.Messages[0].Text);
        Assert.Contains("Echo [2]: Healthy follow-up", second.Messages[0].Text);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
        await AssertNoWorkflowTaskFailuresAsync(handle);
    }

    [Fact]
    public async Task UpdateWithStart_FirstFireAndForgetTurnInitializesBeforeProcessing()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        var response = await _fixture.AgentProxy.RunAsync(
            "First update-with-start",
            session,
            new TemporalAgentRunOptions { IsFireAndForget = true });

        Assert.Empty(response.Messages);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
        await WaitForHistoryEntriesAsync(handle, expectedCount: 2);

        var followUp = await _fixture.AgentProxy.RunAsync("After fire-and-forget", session);
        Assert.Contains("Echo [2]: After fire-and-forget", followUp.Messages[0].Text);
        await AssertNoWorkflowTaskFailuresAsync(handle);
    }

    [Fact]
    public async Task DelayedWorkflowStart_FirstFireAndForgetTurnInitializesBeforeProcessing()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        var proxy = Assert.IsType<TemporalAIAgentProxy>(_fixture.AgentProxy);

        await proxy.RunDelayedAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Delayed first update")],
            session,
            TimeSpan.FromMilliseconds(100));

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
        await WaitForHistoryEntriesAsync(handle, expectedCount: 2);

        var followUp = await _fixture.AgentProxy.RunAsync("After delayed update", session);
        Assert.Contains("Echo [2]: After delayed update", followUp.Messages[0].Text);
        await AssertNoWorkflowTaskFailuresAsync(handle);
    }

    [Fact]
    public async Task FireAndForget_CompletesBeforeSynchronousUpdate()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        // Send synchronous turn 1 to establish the session.
        var r1 = await _fixture.AgentProxy.RunAsync("Turn 1", session);
        Assert.Contains("Echo [1]:", r1.Messages[0].Text);

        var fireAndForgetOptions = new TemporalAgentRunOptions { IsFireAndForget = true };
        var fafResponse = await _fixture.AgentProxy.RunAsync("F&F message", session, fireAndForgetOptions);

        // Fire-and-forget returns empty response.
        Assert.NotNull(fafResponse);
        Assert.Empty(fafResponse.Messages);

        var typedSession = (TemporalAgentSession)session;
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            typedSession.SessionId.WorkflowId);
        await WaitForHistoryEntriesAsync(handle, expectedCount: 4);

        var syncResponse = await _fixture.AgentProxy.RunAsync("Sync message", session);
        Assert.NotEmpty(syncResponse.Messages);

        await WaitForHistoryEntriesAsync(handle, expectedCount: 6);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        Assert.Equal(6, history.Count);

        _output.WriteLine(
            $"Fire-and-forget completed before the synchronous update. History entries: {history.Count}");
    }

    // ── Worker restart & history replay ────────────────────────────────────────

    [Fact]
    public async Task WorkerRestart_HistoryPreservedViaReplay()
    {
        // Build a SEPARATE host so we can control its lifecycle independently.
        // Using a unique task queue avoids interference with the shared fixture worker.
        var taskQueue = $"restart-test-{Guid.NewGuid():N}";
        var host1 = BuildHostForTaskQueue(taskQueue);
        await host1.StartAsync();

        try
        {
            var proxy1 = host1.Services.GetTemporalAgentProxy("EchoAgent");

            // Turn 1 — on host1.
            var session = (TemporalAgentSession)await proxy1.CreateSessionAsync();
            var r1 = await proxy1.RunAsync("Turn 1", session);
            Assert.Contains("Echo [1]: Turn 1", r1.Messages[0].Text);

            _output.WriteLine($"Turn 1 completed on host1. Stopping host1...");

            // Stop host1 (kills the worker — the workflow stays Running on the server).
            await host1.StopAsync();
            host1.Dispose();

            _output.WriteLine("Host1 stopped. Starting host2...");

            // Start a brand new host on the SAME task queue.
            var host2 = BuildHostForTaskQueue(taskQueue);
            await host2.StartAsync();

            try
            {
                var proxy2 = host2.Services.GetTemporalAgentProxy("EchoAgent");

                // Turn 2 — on host2, same session ID.
                // The workflow replays from event history to rebuild state,
                // then processes the new update.
                var resumedSession = new TemporalAgentSession(session.SessionId);
                var r2 = await proxy2.RunAsync("Turn 2", resumedSession);

                // History is preserved: the agent sees 2 user messages.
                Assert.Contains("Echo [2]: Turn 2", r2.Messages[0].Text);

                _output.WriteLine("Turn 2 completed on host2 — history preserved via replay.");
            }
            finally
            {
                await host2.StopAsync();
                host2.Dispose();
            }
        }
        catch
        {
            // If we fail before stopping host1, clean up.
            try { await host1.StopAsync(); } catch { }
            host1.Dispose();
            throw;
        }
    }

    // ── Multiple concurrent sessions ──────────────────────────────────────────

    [Fact]
    public async Task MultipleConcurrentSessions_AreFullyIsolated()
    {
        // Create 3 independent sessions to the same agent.
        // Each session gets its own workflow instance (unique WorkflowId).
        var sessions = await Task.WhenAll(
            _fixture.AgentProxy.CreateSessionAsync().AsTask(),
            _fixture.AgentProxy.CreateSessionAsync().AsTask(),
            _fixture.AgentProxy.CreateSessionAsync().AsTask());

        // Verify all 3 workflow IDs are distinct.
        var workflowIds = sessions
            .Cast<TemporalAgentSession>()
            .Select(s => s.SessionId.WorkflowId)
            .ToList();
        Assert.Equal(3, workflowIds.Distinct().Count());

        // Send messages in parallel — each session should maintain independent state.
        var tasks = sessions.Select(async (session, index) =>
        {
            var r1 = await _fixture.AgentProxy.RunAsync($"Session{index}-Turn1", session);
            Assert.Contains("Echo [1]:", r1.Messages[0].Text);

            var r2 = await _fixture.AgentProxy.RunAsync($"Session{index}-Turn2", session);
            Assert.Contains("Echo [2]:", r2.Messages[0].Text);

            return r2;
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // All 3 sessions completed with correct per-session turn counts.
        Assert.Equal(3, results.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains($"Session{i}-Turn2", results[i].Messages[0].Text);
        }

        _output.WriteLine(
            $"3 concurrent sessions with IDs [{string.Join(", ", workflowIds)}] " +
            "all maintained independent history.");
    }

    // ── Session TTL enforcement ──────────────────────────────────────────────

    [Fact]
    public async Task SessionTTL_WorkflowCompletesAfterExpiry()
    {
        // Register an agent with a very short TTL (2 seconds).
        var taskQueue = $"ttl-test-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.DefaultTimeToLive = TimeSpan.FromSeconds(2);
                options.AddDurableAgent("TTLAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TTLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Send one message to start the workflow.
            var r1 = await proxy.RunAsync("Hello TTL", session);
            Assert.Contains("Echo [1]: Hello TTL", r1.Messages[0].Text);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);

            // Wait for the workflow to complete via TTL expiry.
            // The WaitConditionAsync in RunAsync has a 2-second timeout.
            await handle.GetResultAsync();

            var desc = await handle.DescribeAsync();
            Assert.Equal(WorkflowExecutionStatus.Completed, desc.Status);

            _output.WriteLine(
                $"Workflow {session.SessionId.WorkflowId} completed after TTL expiry.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SessionTTL_NewRequestAfterExpiry_StartsFreshWorkflow()
    {
        // Register an agent with a very short TTL (2 seconds).
        var taskQueue = $"ttl-restart-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.DefaultTimeToLive = TimeSpan.FromSeconds(2);
                options.AddDurableAgent("TTLAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TTLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Send one message to start the workflow.
            await proxy.RunAsync("Before TTL", session);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);

            // Wait for the workflow to complete via TTL.
            await handle.GetResultAsync();

            // Send another request to the same session — should start a fresh workflow.
            // With IdReusePolicy.AllowDuplicate, a new run is allowed after completion.
            var r2 = await proxy.RunAsync("After TTL", session);

            // Fresh workflow has no prior history — turn count resets to 1.
            Assert.Contains("Echo [1]: After TTL", r2.Messages[0].Text);

            _output.WriteLine(
                "Fresh workflow started after TTL expiry; turn count reset to 1.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Builds a host targeting a specific task queue.</summary>
    private IHost BuildHostForTaskQueue(string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => { options.EnableSearchAttributes = false; options.AddDurableAgent("EchoAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient()); });
        return builder.Build();
    }

    private static async Task WaitForHistoryEntriesAsync(
        WorkflowHandle<AgentWorkflow> handle,
        int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var history = await handle.QueryAsync(wf => wf.GetHistory());
            if (history.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        throw new TimeoutException($"Workflow did not record {expectedCount} history entries.");
    }

    private static async Task AssertNoWorkflowTaskFailuresAsync(WorkflowHandle<AgentWorkflow> handle)
    {
        var eventTypes = new List<EventType>();
        await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
        {
            eventTypes.Add(historyEvent.EventType);
        }

        Assert.DoesNotContain(EventType.WorkflowTaskFailed, eventTypes);
        Assert.DoesNotContain(EventType.WorkflowTaskTimedOut, eventTypes);
    }
}
