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
    [Theory]
    [InlineData(DirectAdapterKind.Chat, "activity-queue-chat")]
    [InlineData(DirectAdapterKind.Embedding, "1:3")]
    public async Task DirectAdapterTaskQueue_RoutesToSeparateActivityWorker(
        DirectAdapterKind kind,
        string expected)
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var workflowQueue = $"direct-workflow-{Guid.NewGuid():N}";
        var activityQueue = $"direct-activity-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton<IChatClient>(new ActivityWorkerChatClient());
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new ActivityWorkerEmbeddingGenerator());
        builder.Services
            .AddHostedTemporalWorker(workflowQueue)
            .AddWorkflow<DirectAdapterTaskQueueWorkflow>();
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
                (DirectAdapterTaskQueueWorkflow workflow) => workflow.RunAsync(
                    new DirectAdapterTaskQueueInput(activityQueue, kind)),
                new WorkflowOptions(
                    $"direct-task-queue-{kind}-{Guid.NewGuid():N}",
                    workflowQueue));

            Assert.Equal(expected, await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15)));
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

        public void Dispose()
        {
        }
    }

    private sealed class ActivityWorkerEmbeddingGenerator
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(_ =>
                new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f }));
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

public enum DirectAdapterKind
{
    Chat,
    Embedding,
}

public sealed record DirectAdapterTaskQueueInput(
    string ActivityTaskQueue,
    DirectAdapterKind Kind);

[Workflow("TemporalCommunity.Extensions.AI.Tests.DirectAdapterTaskQueueWorkflow")]
public sealed class DirectAdapterTaskQueueWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(DirectAdapterTaskQueueInput input)
    {
        var durableOptions = new DurableExecutionOptions
        {
            TaskQueue = input.ActivityTaskQueue,
            ActivityTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
        };

        if (input.Kind == DirectAdapterKind.Chat)
        {
            var client = new DurableChatClient(new WorkflowOnlyChatClient(), durableOptions);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")]);
            return response.Text ?? string.Empty;
        }

        var generator = new DurableEmbeddingGenerator(
            new WorkflowOnlyEmbeddingGenerator(),
            durableOptions);
        var embeddings = await generator.GenerateAsync(["hello"]);
        return $"{embeddings.Count}:{embeddings[0].Vector.Length}";
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

        public void Dispose()
        {
        }
    }

    private sealed class WorkflowOnlyEmbeddingGenerator
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Workflow-local embedding provider must not execute.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
