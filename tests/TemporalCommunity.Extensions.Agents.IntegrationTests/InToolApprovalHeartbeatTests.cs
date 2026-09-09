using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// In-tool approval must survive a review that outlasts the tool activity's heartbeat timeout.
/// </summary>
/// <remarks>
/// <para>
/// The package heartbeats once, before the tool body runs. Without a pump during the approval
/// wait, any review longer than the heartbeat timeout killed the activity long before the approval
/// timeout was reached — the documented feature was unusable past roughly two minutes.
/// </para>
/// <para>
/// This runs a <em>real</em> tool activity that blocks on
/// <c>TemporalAgentContext.Current.RequestApprovalAsync</c> for longer than its heartbeat timeout.
/// The existing HITL coverage sends the approval Update straight to the workflow, which never
/// enters a tool activity and so cannot observe this defect at all.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class InToolApprovalHeartbeatTests
{
    private readonly ITestOutputHelper _output;

    public InToolApprovalHeartbeatTests(ITestOutputHelper output) => _output = output;

    /// <summary>Signals the test once the tool has parked on the approval wait.</summary>
    private static readonly TaskCompletionSource ToolParked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task InToolApproval_OutlivingHeartbeatTimeout_StillCompletes()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "publish_draft", new Dictionary<string, object?>())],
            "Publish handled.");

        var taskQueue = $"hb-approval-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                // The crux: the review below runs several times longer than this.
                opts.DefaultHeartbeatTimeout = TimeSpan.FromSeconds(2);
                opts.DefaultActivityTimeout = TimeSpan.FromMinutes(2);
                opts.DefaultApprovalTimeout = TimeSpan.FromMinutes(1);

                opts.AddDurableAgent("Publisher", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(
                        AIFunctionFactory.Create(
                            PublishDraftAsync,
                            new AIFunctionFactoryOptions { Name = "publish_draft" }),
                        tool => tool.NoRetry());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("Publisher");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var run = proxy.RunAsync("Publish the draft.", session);

            // Wait until the tool is actually parked inside the activity, then hold well past the
            // 2s heartbeat timeout before deciding.
            await ToolParked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(7));

            var client = host.Services.GetRequiredService<ITemporalAgentClient>();
            var pending = await client.GetPendingApprovalAsync(session.SessionId);
            Assert.NotNull(pending);

            await client.ResolveApprovalAsync(
                session.SessionId,
                new DurableApprovalDecision
                {
                    RequestId = pending!.RequestId,
                    Approved = true,
                    Reason = "Reviewed after the heartbeat timeout elapsed.",
                });

            var response = await run.WaitAsync(TimeSpan.FromSeconds(60));

            _output.WriteLine(response.Text ?? "(no text)");

            // Reaching here at all is the assertion: without the heartbeat pump the activity
            // would have died at 2s and the turn would never have completed.
            Assert.NotNull(response);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<string> PublishDraftAsync()
    {
        ToolParked.TrySetResult();

        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Publish this draft?",
            });

        return decision.Approved ? "Published" : "Not published";
    }

    /// <summary>Signals when the timeout-path tool has parked.</summary>
    private static readonly TaskCompletionSource TimeoutToolParked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? _timeoutDecision;

    /// <summary>
    /// Keeping the activity alive must not keep it alive forever: an unanswered review still hits
    /// the approval timeout and resolves as a rejection. Pinned so the pump cannot turn a bounded
    /// wait into an unbounded one.
    /// </summary>
    [Fact]
    public async Task InToolApproval_Unanswered_StillTimesOutAsRejection()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "slow_publish", new Dictionary<string, object?>())],
            "Handled.");

        var taskQueue = $"hb-timeout-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                // Heartbeat pump active, and an approval window shorter than the activity timeout.
                opts.DefaultHeartbeatTimeout = TimeSpan.FromSeconds(2);
                opts.DefaultActivityTimeout = TimeSpan.FromMinutes(2);
                opts.DefaultApprovalTimeout = TimeSpan.FromSeconds(6);

                opts.AddDurableAgent("SlowPublisher", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(
                        AIFunctionFactory.Create(
                            SlowPublishAsync,
                            new AIFunctionFactoryOptions { Name = "slow_publish" }),
                        tool => tool.NoRetry());
                });
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("SlowPublisher");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Nobody reviews. The approval timeout must fire even though the pump keeps the
            // activity healthy past its 2s heartbeat timeout.
            var response = await proxy.RunAsync("Publish the draft.", session)
                .WaitAsync(TimeSpan.FromSeconds(90));

            Assert.NotNull(response);
            Assert.Equal("Not published", _timeoutDecision);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<string> SlowPublishAsync()
    {
        TimeoutToolParked.TrySetResult();

        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Publish this draft?",
            });

        _timeoutDecision = decision.Approved ? "Published" : "Not published";
        return _timeoutDecision;
    }
}
