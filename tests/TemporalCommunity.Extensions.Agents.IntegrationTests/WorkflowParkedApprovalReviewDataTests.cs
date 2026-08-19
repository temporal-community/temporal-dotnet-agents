using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Tools;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class WorkflowParkedApprovalReviewDataTests
{
    [Fact]
    public async Task WorkflowParkedApproval_ExposesOnlyExplicitReviewData()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var invocations = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref invocations);
                return "transfer complete";
            },
            name: "transfer_funds",
            description: "Transfers funds.");
        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "call-1",
                "transfer_funds",
                new Dictionary<string, object?> { ["accountNumber"] = "secret-account-123" })],
            "Transfer complete.");

        var taskQueue = $"agent-review-data-{Guid.NewGuid():N}";
        using var host = BuildHost(
            environment.Client,
            taskQueue,
            chatClient,
            tool,
            new ApprovalInterceptor(new Dictionary<string, string>
            {
                ["recipient"] = "payroll",
                ["policy"] = "finance-review",
            }));
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("ApprovalAgent");
        var client = host.Services.GetRequiredService<ITemporalAgentClient>();
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var runTask = proxy.RunAsync("Pay payroll", session);

        var pending = await WaitForPendingAsync(client, session.SessionId);
        Assert.NotNull(pending);
        Assert.Equal(2, pending!.ReviewData!.Count);
        Assert.Equal("payroll", pending.ReviewData["recipient"]);
        Assert.Equal("finance-review", pending.ReviewData["policy"]);
        Assert.DoesNotContain("accountNumber", pending.ReviewData.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-account-123", pending.ReviewData.Values);
        Assert.Equal(0, Volatile.Read(ref invocations));

        Assert.Equal(
            DurableApprovalResolutionStatus.Accepted,
            (await client.ResolveApprovalAsync(session.SessionId, new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                Approved = true,
            })).Status);

        await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref invocations));
        await host.StopAsync();
    }

    [Fact]
    public async Task WorkflowParkedApproval_LeavesReviewDataNullWithoutInterceptorMetadata()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var tool = AIFunctionFactory.Create(
            () => "transfer complete",
            name: "transfer_funds",
            description: "Transfers funds.");
        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "call-1",
                "transfer_funds",
                new Dictionary<string, object?> { ["accountNumber"] = "secret-account-123" })],
            "Transfer complete.");

        var taskQueue = $"agent-review-data-null-{Guid.NewGuid():N}";
        using var host = BuildHost(environment.Client, taskQueue, chatClient, tool, new ApprovalInterceptor(null));
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("ApprovalAgent");
        var client = host.Services.GetRequiredService<ITemporalAgentClient>();
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var runTask = proxy.RunAsync("Pay payroll", session);

        var pending = await WaitForPendingAsync(client, session.SessionId);
        Assert.NotNull(pending);
        Assert.Null(pending!.ReviewData);

        await client.ResolveApprovalAsync(session.SessionId, new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = false,
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync();
    }

    [Fact]
    public async Task WorkerRestart_WhileApprovalPending_PreservesRequestIdAndRunsToolOnce()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var invocations = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref invocations);
                return "done";
            },
            name: "write_record",
            description: "Writes a record.");
        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "write_record")],
            "Write complete.");
        var taskQueue = $"agent-approval-restart-{Guid.NewGuid():N}";

        using var host1 = BuildHost(environment.Client, taskQueue, chatClient, tool, new ApprovalInterceptor(null));
        await host1.StartAsync();
        var proxy = host1.Services.GetTemporalAgentProxy("ApprovalAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var runTask = proxy.RunAsync("write", session);
        var pendingBeforeRestart = await WaitForPendingAsync(
            host1.Services.GetRequiredService<ITemporalAgentClient>(), session.SessionId);
        Assert.NotNull(pendingBeforeRestart);
        Assert.Equal(0, Volatile.Read(ref invocations));

        await host1.StopAsync();
        using var host2 = BuildHost(environment.Client, taskQueue, chatClient, tool, new ApprovalInterceptor(null));
        await host2.StartAsync();

        var client2 = host2.Services.GetRequiredService<ITemporalAgentClient>();
        var pendingAfterRestart = await WaitForPendingAsync(client2, session.SessionId);
        Assert.NotNull(pendingAfterRestart);
        Assert.Equal(pendingBeforeRestart!.RequestId, pendingAfterRestart!.RequestId);

        await client2.ResolveApprovalAsync(session.SessionId, new DurableApprovalDecision
        {
            RequestId = pendingAfterRestart.RequestId,
            Approved = true,
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref invocations));

        var handle = environment.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
        var schedules = 0;
        await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
        {
            if (historyEvent.ActivityTaskScheduledEventAttributes?.ActivityType.Name ==
                "TemporalCommunity.Extensions.Agents.InvokeAgentTool")
            {
                schedules++;
            }
        }
        Assert.Equal(1, schedules);
        await host2.StopAsync();
    }

    [Fact]
    public async Task WorkflowParkedApproval_ConcurrentEquivalentResolutions_RunToolOnce()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var invocations = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref invocations);
                return "done";
            },
            name: "write_record",
            description: "Writes a record.");
        var chatClient = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("call-1", "write_record")],
            "Write complete.");
        var taskQueue = $"agent-approval-race-{Guid.NewGuid():N}";

        using var host = BuildHost(environment.Client, taskQueue, chatClient, tool, new ApprovalInterceptor(null));
        await host.StartAsync();
        var proxy = host.Services.GetTemporalAgentProxy("ApprovalAgent");
        var client = host.Services.GetRequiredService<ITemporalAgentClient>();
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var runTask = proxy.RunAsync("write", session);
        var pending = await WaitForPendingAsync(client, session.SessionId);
        Assert.NotNull(pending);

        var decision = new DurableApprovalDecision
        {
            RequestId = pending!.RequestId,
            Approved = true,
            Reason = "Approved by reviewer.",
        };
        var outcomes = await Task.WhenAll(
            client.ResolveApprovalAsync(session.SessionId, decision),
            client.ResolveApprovalAsync(session.SessionId, decision));

        Assert.Contains(outcomes, outcome => outcome.Status == DurableApprovalResolutionStatus.Accepted);
        Assert.Contains(outcomes, outcome => outcome.Status == DurableApprovalResolutionStatus.AlreadyResolved);
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref invocations));
        await host.StopAsync();
    }

    private static async Task<DurableApprovalRequest?> WaitForPendingAsync(
        ITemporalAgentClient client,
        TemporalAgentSessionId sessionId)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var pending = await client.GetPendingApprovalAsync(sessionId);
                if (pending is not null)
                {
                    return pending;
                }
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // The first query can arrive before the asynchronous workflow start is visible.
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static IHost BuildHost(
        ITemporalClient client,
        string taskQueue,
        IChatClient chatClient,
        AIFunction tool,
        IAgentToolInterceptor interceptor)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(chatClient);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
                options.AddDurableAgent("ApprovalAgent", agent =>
                {
                    agent.ChatClient = services => services.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, toolOptions => toolOptions.RequireApproval());
                    agent.AddToolInterceptor(_ => interceptor);
                });
            });
        return builder.Build();
    }

    private sealed class ApprovalInterceptor(IReadOnlyDictionary<string, string>? metadata) : IAgentToolInterceptor
    {
        public Task<DurableToolDecision> BeforeToolCallAsync(
            AgentToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableToolDecision.PauseForApproval("Review the transfer.", metadata));
    }
}
