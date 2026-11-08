using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Tests.Shared;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class WorkflowActivityAgentInvocationTests
{
    [Fact]
    public async Task WorkflowInvokesRegisteredAgentThroughActivities_WithStableRetryIdentity()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var chatClient = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "awaited result")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "fire-and-forget result")),
        ]);
        var interceptor = new FailFirstAgentInvocationActivity();
        var queue = $"workflow-agent-activity-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddChatClient(chatClient);
        builder.Services
            .AddHostedTemporalWorker(queue)
            .ConfigureOptions(options => options.Interceptors = [interceptor])
            .AddWorkflow<WorkflowActivityAgentInvocationWorkflow>()
            .AddTemporalAgents(options => options.AddDurableAgent("ActivityAgent", agent =>
                agent.ChatClient = services => services.GetRequiredService<IChatClient>()))
            .AddScopedActivities<WorkflowActivityAgentInvocationActivities>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (WorkflowActivityAgentInvocationWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(
                    $"workflow-agent-activity-{Guid.NewGuid():N}",
                    queue));
            var result = await handle.GetResultAsync();

            Assert.Equal("awaited result", result.Response);
            var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();
            var outcome = await agentClient.GetTurnOutcomeAsync(
                result.FireAndForgetSessionId,
                result.FireAndForgetRequestId);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (outcome.Status == TemporalAgentTurnOutcomeStatus.Pending
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
                outcome = await agentClient.GetTurnOutcomeAsync(
                    result.FireAndForgetSessionId,
                    result.FireAndForgetRequestId);
            }

            Assert.Equal(TemporalAgentTurnOutcomeStatus.Succeeded, outcome.Status);
            Assert.Equal(2, chatClient.CallCount);
            Assert.Equal(1, interceptor.InjectedFailures);
            Assert.Equal(2, interceptor.FirstActivityAttempts);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("WorkflowActivityAgentInvocation.Workflow")]
    internal sealed class WorkflowActivityAgentInvocationWorkflow
    {
        [WorkflowRun]
        public async Task<WorkflowActivityAgentInvocationResult> RunAsync()
        {
            var awaitedSession = WorkflowAgents.NewAgentSessionId("ActivityAgent");
            var awaitedRequestId = Workflow.NewGuid().ToString("N");
            var response = await Workflow.ExecuteActivityAsync(
                (WorkflowActivityAgentInvocationActivities activities) => activities.RunAgentAsync(
                    awaitedSession.WorkflowId,
                    awaitedRequestId,
                    "awaited"),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(2),
                    RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 2 },
                }).ConfigureAwait(true);

            var fireAndForgetSession = WorkflowAgents.NewAgentSessionId("ActivityAgent");
            var fireAndForgetRequestId = Workflow.NewGuid().ToString("N");
            await Workflow.ExecuteActivityAsync(
                (WorkflowActivityAgentInvocationActivities activities) => activities.RunFireAndForgetAsync(
                    fireAndForgetSession.WorkflowId,
                    fireAndForgetRequestId,
                    "fire-and-forget"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(true);

            return new WorkflowActivityAgentInvocationResult(
                response,
                fireAndForgetSession,
                fireAndForgetRequestId);
        }
    }

    internal sealed record WorkflowActivityAgentInvocationResult(
        string Response,
        TemporalAgentSessionId FireAndForgetSessionId,
        string FireAndForgetRequestId);

    internal sealed class WorkflowActivityAgentInvocationActivities(IServiceProvider services)
    {
        [Activity("Tests.RunAgentFromWorkflow")]
        public async Task<string> RunAgentAsync(string sessionWorkflowId, string requestId, string message)
        {
            var session = new TemporalAgentSession(TemporalAgentSessionId.Parse(sessionWorkflowId));
            AIAgent agent = services.GetTemporalAgentProxy("ActivityAgent");
            var response = await agent.RunAsync(
                message,
                session,
                new TemporalAgentRunOptions { RequestId = requestId }).ConfigureAwait(false);
            return response.Text ?? string.Empty;
        }

        [Activity]
        public async Task RunFireAndForgetAsync(string sessionWorkflowId, string requestId, string message)
        {
            var session = new TemporalAgentSession(TemporalAgentSessionId.Parse(sessionWorkflowId));
            AIAgent agent = services.GetTemporalAgentProxy("ActivityAgent");
            await agent.RunAsync(
                message,
                session,
                new TemporalAgentRunOptions { RequestId = requestId, IsFireAndForget = true })
                .ConfigureAwait(false);
        }
    }

    private sealed class FailFirstAgentInvocationActivity : IWorkerInterceptor
    {
        private int _attempts;
        private int _failures;

        public int FirstActivityAttempts => Volatile.Read(ref _attempts);
        public int InjectedFailures => Volatile.Read(ref _failures);

        public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
            new FailFirstActivityInbound(nextInterceptor, this);

        private sealed class FailFirstActivityInbound(
            ActivityInboundInterceptor next,
            FailFirstAgentInvocationActivity owner) : ActivityInboundInterceptor(next)
        {
            public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
            {
                if (ActivityExecutionContext.Current.Info.ActivityType != "Tests.RunAgentFromWorkflow")
                {
                    return await base.ExecuteActivityAsync(input);
                }

                var attempt = Interlocked.Increment(ref owner._attempts);
                var result = await base.ExecuteActivityAsync(input);
                if (attempt == 1)
                {
                    Interlocked.Increment(ref owner._failures);
                    throw new InvalidOperationException("Injected failure after the agent proxy completed.");
                }

                return result;
            }
        }
    }
}
