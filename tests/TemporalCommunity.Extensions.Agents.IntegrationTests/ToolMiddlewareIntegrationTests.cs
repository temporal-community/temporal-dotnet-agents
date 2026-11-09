using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public class ToolMiddlewareIntegrationTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ToolMiddleware_ApprovalBindingRetryAndReplay(
        bool approve, bool loseCompletion, bool mutateArguments)
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var callbackIds = new ConcurrentQueue<string>();
        var scopes = new ConcurrentQueue<ScopeMarker>();
        var effects = 0;
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string value, IServiceProvider services) =>
            {
                Assert.Equal("approved", value);
                Assert.Same(scopes.Last(), services.GetRequiredService<ScopeMarker>());
                Interlocked.Increment(ref effects);
                return value;
            }, "write"));
        var model = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("approved-call", "write",
                new Dictionary<string, object?> { ["value"] = "approved" })], "done");
        var taskQueue = $"tool-middleware-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddScoped<ScopeMarker>();
        builder.Services.AddHostedTemporalWorker(taskQueue)
            .ConfigureOptions(options =>
                options.Interceptors = loseCompletion ? [new LostToolCompletionInterceptor()] : [])
            .AddTemporalAgents(agents =>
            {
                agents.EnableSearchAttributes = false;
                agents.Agent("MiddlewareAgent", _ => model,
                    new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } },
                    temporal =>
                    {
                        temporal.RetryPolicy = new RetryPolicy
                        {
                            MaximumAttempts = 2,
                            InitialInterval = TimeSpan.FromMilliseconds(10),
                        };
                        temporal.ToolMiddleware = async (agent, context, next, ct) =>
                        {
                            Assert.Equal("MiddlewareAgent", agent.Name);
                            Assert.Equal(1, context.Iteration);
                            Assert.Equal(0, context.FunctionCallIndex);
                            Assert.Equal(1, context.FunctionCount);
                            Assert.NotEmpty(context.Messages);
                            callbackIds.Enqueue(context.CallContent.CallId);
                            scopes.Enqueue(context.Arguments.Services!.GetRequiredService<ScopeMarker>());
                            if (mutateArguments)
                                context.Arguments["value"] = "not approved";
                            return $"middleware:{await next(context, ct)}";
                        };
                    });
            });
        using var host = builder.Build();
        await host.StartAsync();
        var proxy = host.Services.GetRequiredKeyedService<AIAgent>("MiddlewareAgent");
        var client = host.Services.GetRequiredService<ITemporalAgentClient>();
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var runOptions = new TemporalAgentRunOptions { RequestId = "middleware-request" };
        try
        {
            var run = proxy.RunAsync("write", session, runOptions);
            var pending = await WaitForPendingApprovalAsync(client, session.SessionId);
            Assert.NotNull(pending);
            Assert.Empty(callbackIds);
            Assert.Equal(0, effects);
            await client.ResolveApprovalAsync(session.SessionId, new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                FunctionName = pending.FunctionName,
                CallId = pending.CallId,
                Approved = approve,
            });

            if (mutateArguments)
            {
                await Assert.ThrowsAsync<DurableTurnFailedException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(30)));
                await Assert.ThrowsAsync<DurableTurnFailedException>(
                    () => proxy.RunAsync("write", session, runOptions));
                Assert.Single(callbackIds);
                Assert.Equal(0, effects);
                Assert.Equal(1, model.CallCount);
            }
            else
            {
                var response = await run.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal("done", response.Text);
                var replayed = await proxy.RunAsync("write", session, runOptions);
                Assert.Equal(response.Text, replayed.Text);
                Assert.Equal(approve ? loseCompletion ? 2 : 1 : 0, effects);
                Assert.Equal(effects, callbackIds.Count);
                Assert.All(callbackIds, id => Assert.Equal("approved-call", id));
                Assert.Equal(2, model.CallCount);
                var result = Assert.Single(model.Calls[1].Messages
                    .SelectMany(message => message.Contents).OfType<FunctionResultContent>());
                if (approve) Assert.Equal("middleware:approved", result.Result?.ToString());
            }
            Assert.All(scopes, marker => Assert.True(marker.Disposed));
            Assert.Equal(scopes.Count, scopes.Distinct().Count());
            await client.ShutdownAsync(session.SessionId);
            var workflow = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var callbackCount = callbackIds.Count;
            var modelCount = model.CallCount;
            var replayOptions = new WorkflowReplayerOptions { DataConverter = TemporalAgentDataConverter.Instance };
            replayOptions.AddWorkflow(AgentWorkflowWorkerFactory.CreateDefinition(
                host.Services.GetRequiredService<TemporalAgentsOptions>(),
                taskQueue));
            var replay = await new WorkflowReplayer(replayOptions).ReplayWorkflowAsync(
                await workflow.FetchHistoryAsync(), throwOnReplayFailure: false);
            Assert.Null(replay.ReplayFailure);
            Assert.Equal(callbackCount, callbackIds.Count);
            Assert.Equal(modelCount, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ToolMiddleware_TerminationSkipsRemainingCallsAndNextModelRound()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var effects = 0;
        var callbacks = 0;
        var model = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent("first", "lookup", new Dictionary<string, object?>()),
             new FunctionCallContent("second", "lookup", new Dictionary<string, object?>())], "must not run");
        var tool = AIFunctionFactory.Create(() => ++effects, "lookup");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddHostedTemporalWorker($"middleware-stop-{Guid.NewGuid():N}")
            .AddTemporalAgents(agents =>
            {
                agents.EnableSearchAttributes = false;
                agents.Agent("StopAgent", _ => model,
                    new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } },
                    temporal =>
                    {
                        temporal.ToolMiddleware = (_, context, _, _) =>
                        {
                            callbacks++;
                            context.Terminate = true;
                            return new ValueTask<object?>("cached");
                        };
                    });
            });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredKeyedService<AIAgent>("StopAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("lookup twice", session,
                new TemporalAgentRunOptions { RequestId = "stop-turn" });
            var results = response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().ToArray();
            Assert.Equal(2, results.Length);
            Assert.Equal("cached", results[0].Result?.ToString());
            Assert.Contains("terminated", results[1].Result?.ToString());
            Assert.Equal(1, callbacks);
            Assert.Equal(0, effects);
            Assert.Equal(1, model.CallCount);
            await proxy.RunAsync("lookup twice", session,
                new TemporalAgentRunOptions { RequestId = "stop-turn" });
            Assert.Equal(1, callbacks);
            Assert.Equal(1, model.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class ScopeMarker : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    private static async Task<DurableApprovalRequest?> WaitForPendingApprovalAsync(
        ITemporalAgentClient client,
        TemporalAgentSessionId sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var pending = await client.GetPendingApprovalAsync(sessionId);
                if (pending is not null)
                    return pending;
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // The first query may arrive before the asynchronous workflow start is visible.
            }

            await Task.Delay(50);
        }

        return null;
    }

    private sealed class LostToolCompletionInterceptor : IWorkerInterceptor
    {
        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new LostToolCompletionInbound(nextInterceptor);
    }

    private sealed class LostToolCompletionInbound(ActivityInboundInterceptor next) : ActivityInboundInterceptor(next)
    {
        public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
        {
            var result = await base.ExecuteActivityAsync(input);
            var info = ActivityExecutionContext.Current.Info;
            if (info.ActivityType == "TemporalCommunity.Extensions.Agents.InvokeAgentTool" && info.Attempt == 1)
                throw new InvalidOperationException("Injected lost tool completion after its effect.");
            return result;
        }
    }
}
