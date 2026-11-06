using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

[Trait("Category", "Integration")]
public class WorkerModelContextBudgetTests
{
    private const string AgentName = "BudgetAgent";

    [Theory]
    [InlineData(null)]
    [InlineData(100000)]
    [InlineData(1)]
    public async Task SplitProxy_WorkerBudgetAppliesBeforeAndAfterResolution(int? frontendLimit)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var queue = $"split-budget-{Guid.NewGuid():N}";
        var client = new RecordingChatClient();
        using var worker = BuildWorker(environment.Client, queue, client);
        await worker.StartAsync();
        try
        {
            var frontend = Host.CreateApplicationBuilder();
            frontend.Services.AddSingleton<ITemporalClient>(environment.Client);
            frontend.Services.AddTemporalAgentProxies(options =>
            {
                options.DefaultMaxModelContextTokens = frontendLimit;
                options.DefaultModelOutputTokenReserve = 0;
                options.EnableSearchAttributes = false;
                options.AddAgentProxy(AgentName);
            }, taskQueue: queue);
            using var frontendHost = frontend.Build();
            var proxy = frontendHost.Services.GetTemporalAgentProxy(AgentName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var oversizedSession = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var firstFailure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                () => proxy.RunAsync(
                    new string('x', 70), oversizedSession, cancellationToken: deadline.Token));
            AssertBudgetFailure(firstFailure);
            Assert.Equal(0, client.CallCount);

            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("small", session, cancellationToken: deadline.Token);
            Assert.Equal("done", response.Messages[^1].Text);
            Assert.Equal(1, client.CallCount);

            var laterFailure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                () => proxy.RunAsync(
                    new string('x', 70), session, cancellationToken: deadline.Token));
            AssertBudgetFailure(laterFailure);
            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            await worker.StopAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SubagentAndJob_EnforceWorkerBudget_OnFirstAndLaterModelSteps(
        bool job,
        bool afterTool)
    {
        await using var environment = await TestEnvironmentHelper.StartLocalAsync();
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var queue = $"dispatch-budget-{Guid.NewGuid():N}";
        var client = new RecordingChatClient(returnToolCall: afterTool);
        using var worker = BuildWorker(environment.Client, queue, client);
        await worker.StartAsync();
        try
        {
            var text = afterTool ? "small" : new string('x', 70);
            WorkflowHandle handle;
            if (job)
            {
                var input = DefaultTemporalAgentClient.BuildAgentJobInput(
                    AgentName,
                    new RunRequest(text),
                    worker.Services.GetRequiredService<TemporalAgentsOptions>(),
                    queue);
                handle = await environment.Client.StartWorkflowAsync(
                    (AgentJobWorkflow workflow) => workflow.RunAsync(input),
                    new WorkflowOptions($"ta-BudgetAgent-scheduled-{Guid.NewGuid():N}", queue));
            }
            else
            {
                handle = await environment.Client.StartWorkflowAsync(
                    (BudgetSubagentWorkflow workflow) => workflow.RunAsync(text),
                    new WorkflowOptions($"budget-parent-{Guid.NewGuid():N}", queue));
            }

            if (job)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
                    () => handle.GetResultAsync(
                        rpcOptions: new RpcOptions { CancellationToken = deadline.Token }));
                AssertBudgetFailure(failure);
            }
            else
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var history = await handle.FetchHistoryAsync();
                while (!history.Events.Any(item =>
                    item.ActivityTaskFailedEventAttributes?.Failure.ApplicationFailureInfo?.Type
                    == "DurableContextBudgetExceeded"))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
                    history = await handle.FetchHistoryAsync();
                }

                var failedStep = Assert.Single(history.Events, item =>
                    item.ActivityTaskFailedEventAttributes?.Failure.ApplicationFailureInfo?.Type
                    == "DurableContextBudgetExceeded").ActivityTaskFailedEventAttributes!;
                Assert.True(failedStep.Failure.ApplicationFailureInfo.NonRetryable);
                Assert.Equal(afterTool ? 2 : 1, history.Events.Count(item =>
                    item.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                    == "TemporalCommunity.Extensions.Agents.RunDurableAgentStep"));
            }
            Assert.Equal(afterTool ? 1 : 0, client.CallCount);
        }
        finally
        {
            await worker.StopAsync();
        }
    }

    private static IHost BuildWorker(ITemporalClient temporalClient, string queue, RecordingChatClient client)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(temporalClient);
        builder.Services.AddSingleton<IDurableChatTokenEstimator>(new TextLengthEstimator());
        builder.Services.AddHostedTemporalWorker(queue)
            .AddWorkflow<BudgetSubagentWorkflow>()
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.DefaultMaxModelContextTokens = 100;
                options.DefaultModelOutputTokenReserve = 40;
                options.AddDurableAgent(AgentName, agent =>
                {
                    agent.ChatClient = _ => client;
                    agent.AddTool(AIFunctionFactory.Create(() => new string('t', 70), "large_result"));
                });
            });
        return builder.Build();
    }

    private static void AssertBudgetFailure(Exception failure)
    {
        Exception? current = failure;
        while (current is not null)
        {
            if (current is ApplicationFailureException { ErrorType: "DurableContextBudgetExceeded" } application)
            {
                Assert.True(application.NonRetryable);
                return;
            }

            current = current.InnerException;
        }

        Assert.Fail($"Expected a model-context budget failure, got: {failure}");
    }

    [Workflow("WorkerModelContextBudget.Subagent")]
    internal sealed class BudgetSubagentWorkflow
    {
        [WorkflowRun]
        public async Task RunAsync(string text)
        {
            var agent = WorkflowAgents.GetTemporalAgent(AgentName);
            var session = await agent.CreateSessionAsync();
            await agent.RunAsync(text, session);
        }
    }

    private sealed class TextLengthEstimator : IDurableChatTokenEstimator
    {
        public int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options) =>
            messages.Sum(message => message.Text?.Length ?? 0)
            + messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Sum(content => content.Result?.ToString()?.Length ?? 0);
    }

    private sealed class RecordingChatClient(bool returnToolCall = false) : IChatClient
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var message = returnToolCall
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "large_result", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(message));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var message = returnToolCall
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "large_result", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "done");
            foreach (var update in new ChatResponse(message).ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
