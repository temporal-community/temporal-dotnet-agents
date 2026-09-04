using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.AI;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// C-5 (Wave 4) — proxy / split-deployment worker-settings resolution must survive a transient
/// first-step failure. The first <c>RunDurableAgentStep</c> activity (which carries the resolution
/// handshake on the proxy-started path) fails transiently; Temporal retries the activity, and the
/// resolved config (e.g. <c>MaxToolCallsPerTurn</c>) is applied correctly on a later step — not
/// lost or reset to the hard-coded default.
///
/// <para>
/// We assert the resolved cap is honored on turn 2: a wrapping chat client throws on its very first
/// call (failing the first activity attempt) then returns scripted responses. The agent is
/// registered with <c>MaxToolCallsPerTurn = 2</c>; turn 2 scripts a loop tool. If resolution were
/// lost the cap would reset to 20 and the scripted client would be over-consumed.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ProxyResolutionFailureTests
{
    [Fact]
    public async Task FirstStepTransientFailure_ResolutionAppliedOnLaterStep()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        const int resolvedCap = 2;
        var loopTool = new FunctionCallContent("call-loop", "loop_tool",
            new Dictionary<string, object?> { ["input"] = "go" });

        var scripted = new ScriptedChatClient(
        [
            // Turn 1: final answer (resolves settings; cap = resolvedCap).
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn1Done")),
            // Turn 2: resolvedCap loop iterations, then a sentinel that must NOT be reached.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [loopTool])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ShouldNotBeReached")),
        ]);

        // Wrap so the FIRST chat call (first RunDurableAgentStep attempt) throws transiently.
        var transient = new FailFirstCallChatClient(scripted, failCalls: 1);

        var taskQueue = $"proxy-resolution-failure-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(transient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("ResolveAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.MaxToolCallsPerTurn = resolvedCap;
                    agent.AddTool(AIFunctionFactory.Create(() => "looped", "loop_tool"));
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ResolveAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Turn 1: the first step's activity fails once (transient), Temporal retries, then the
            // resolution + chat succeed. The turn completes despite the transient failure.
            var r1 = await proxy.RunAsync("turn 1", session);
            Assert.Contains("Turn1Done", r1.Messages[^1].Text);
            Assert.True(transient.FailuresTriggered >= 1, "Expected a transient first-call failure.");

            // Turn 2: loop tool. If the resolved cap (2) was correctly applied despite the
            // first-step failure, the loop stops at 2 dispatches and the "ShouldNotBeReached"
            // response is never consumed. If resolution were lost (reset to 20), the scripted
            // client would throw on over-consumption and this call would fail.
            var r2 = await proxy.RunAsync("turn 2", session);
            Assert.NotNull(r2);

            await host.StopAsync();
        }
        catch
        {
            await host.StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Wraps an inner <see cref="IChatClient"/> and throws on its first <paramref name="failCalls"/>
    /// calls (simulating a transient outage on the first RunDurableAgentStep activity attempt),
    /// then delegates to the inner client.
    /// </summary>
    private sealed class FailFirstCallChatClient(IChatClient inner, int failCalls) : IChatClient
    {
        private int _calls;
        private int _failures;

        public int FailuresTriggered => Volatile.Read(ref _failures);

        public ChatClientMetadata Metadata { get; } = new("fail-first");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= failCalls)
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("Simulated transient failure on first chat call.");
            }
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var r = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var m in r.Messages) yield return new ChatResponseUpdate(m.Role, m.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Proxy_InsideWorkflow_ThrowsInvalidOperationException()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var dummyClient = new DummyTemporalAgentClient();
        var proxy = new TemporalAIAgentProxy("TestAgent", dummyClient);

        var taskQueue = $"proxy-in-workflow-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<TestProxyInWorkflow>();
        using var host = builder.Build();
        await host.StartAsync();

        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(async () =>
        {
            await env.Client.ExecuteWorkflowAsync(
                (TestProxyInWorkflow wf) => wf.ExecuteAsync(proxy),
                new WorkflowOptions($"wf-{Guid.NewGuid():N}", taskQueue));
        });
        var innerEx = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("TemporalAIAgentProxy cannot be invoked from inside a Temporal workflow", innerEx.Message);
    }

    private sealed class DummyTemporalAgentClient : ITemporalAgentClient
    {
        public Task<AgentResponse> SendAsync(TemporalAgentSessionId session, RunRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task RunAgentFireAndForgetAsync(TemporalAgentSessionId session, RunRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<TemporalCommunity.Extensions.AI.Approvals.DurableApprovalRequest?> GetPendingApprovalAsync(TemporalAgentSessionId session, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<TemporalCommunity.Extensions.AI.Approvals.DurableApprovalResolutionResult> ResolveApprovalAsync(TemporalAgentSessionId session, TemporalCommunity.Extensions.AI.Approvals.DurableApprovalDecision decision, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task RunAgentDelayedAsync(TemporalAgentSessionId session, RunRequest request, TimeSpan delay, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<Temporalio.Client.Schedules.ScheduleHandle> ScheduleAgentAsync(string agentName, string scheduleId, RunRequest request, Temporalio.Client.Schedules.ScheduleSpec spec, Temporalio.Client.Schedules.SchedulePolicy? policy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Temporalio.Client.Schedules.ScheduleHandle GetAgentScheduleHandle(string scheduleId)
            => throw new NotImplementedException();

        public Task ShutdownAsync(TemporalAgentSessionId session, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Temporalio.Workflows.Workflow]
    internal sealed class TestProxyInWorkflow
    {
        [Temporalio.Workflows.WorkflowRun]
        public async Task ExecuteAsync(TemporalAIAgentProxy proxy)
        {
            await ((AIAgent)proxy).RunAsync([new ChatMessage(ChatRole.User, "Hello")]);
        }
    }
}
