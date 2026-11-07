using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;
using static TemporalCommunity.Extensions.Agents.WorkflowAgents;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class WorkflowLocalApprovalFailClosedTests
{
    [Fact]
    public async Task ProviderSelectedApprovalRequiredTool_IsBlockedWithoutSchedulingToolActivity()
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var toolInvocations = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref toolInvocations);
                return "protected operation executed";
            },
            new AIFunctionFactoryOptions { Name = "protected_operation" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(
            [new FunctionCallContent(
                "approval-call-1",
                "protected_operation",
                new Dictionary<string, object?>())],
            "The protected operation was blocked.");

        var taskQueue = $"subagent-approval-fail-closed-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<ApprovalRequiredWorkflow>()
            .AddTemporalAgents(options => options.AddDurableAgent("SubAgent", agent =>
            {
                agent.ChatClient = services => services.GetRequiredService<IChatClient>();
                agent.AddContextProvider(
                    new EmptyContextProvider(),
                    [new DurableToolRegistrationSpec(
                        tool,
                        toolOptions => toolOptions.RequireApproval())]);
            }));

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (ApprovalRequiredWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions($"subagent-approval-{Guid.NewGuid():N}", taskQueue));
            await handle.GetResultAsync();

            Assert.Equal(0, toolInvocations);
            Assert.Equal(2, scripted.CallCount);
            Assert.Contains(
                scripted.Calls[1].Messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains(
                    "requires approval",
                    StringComparison.OrdinalIgnoreCase) == true);

            var toolSchedules = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                    == "TemporalCommunity.Extensions.Agents.InvokeAgentTool")
                {
                    toolSchedules++;
                }
            }

            Assert.Equal(0, toolSchedules);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Workflow("WorkflowLocalApprovalFailClosed.Workflow")]
    internal sealed class ApprovalRequiredWorkflow
    {
        [WorkflowRun]
        public async Task RunAsync()
        {
            var agent = GetTemporalAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "Attempt the protected operation.")],
                session).ConfigureAwait(true);
        }
    }

    private sealed class EmptyContextProvider : AIContextProvider
    {
        public EmptyContextProvider()
            : base(provideInputMessageFilter: null)
        {
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AIContext());
    }
}
