using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class DirectAdapterTaskQueueIntegrationTests
{
    [Fact]
    public async Task DirectChatClientTaskQueue_RoutesToSeparateActivityWorker()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var workflowQueue = $"direct-workflow-{Guid.NewGuid():N}";
        var activityQueue = $"direct-activity-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton<IChatClient>(new ActivityWorkerChatClient());
        builder.Services
            .AddHostedTemporalWorker(workflowQueue)
            .AddWorkflow<DirectChatTaskQueueWorkflow>();
        builder.Services
            .AddHostedTemporalWorker(activityQueue)
            .AddDurableAI(options =>
            {
                options.TaskQueue = activityQueue;
                options.RegisterDefaultWorkflow = false;
                options.ActivityTimeout = TimeSpan.FromSeconds(10);
                options.HeartbeatTimeout = TimeSpan.FromSeconds(5);
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (DirectChatTaskQueueWorkflow workflow) => workflow.RunAsync(activityQueue),
                new WorkflowOptions(
                    $"direct-task-queue-{Guid.NewGuid():N}",
                    workflowQueue));

            Assert.Equal(
                "activity-queue-chat",
                await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class ActivityWorkerChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "activity-queue-chat")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "activity-queue-chat");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

[Workflow("TemporalCommunity.Extensions.AI.Tests.DirectChatTaskQueueWorkflow")]
public sealed class DirectChatTaskQueueWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string activityTaskQueue)
    {
        var client = new DurableChatClient(new WorkflowOnlyChatClient(), new DurableExecutionOptions
        {
            TaskQueue = activityTaskQueue,
            ActivityTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
        });
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]);
        return response.Text ?? string.Empty;
    }

    private sealed class WorkflowOnlyChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Workflow-local chat provider must not execute.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Workflow-local chat provider must not execute.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
