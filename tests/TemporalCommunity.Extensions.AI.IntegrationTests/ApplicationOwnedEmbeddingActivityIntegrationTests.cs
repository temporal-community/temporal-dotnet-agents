using Microsoft.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public sealed class ApplicationOwnedEmbeddingActivityIntegrationTests
{
    [Fact]
    public async Task WorkflowActivity_UsesInjectedGeneratorAndForwardsActivityCancellationToken()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        var taskQueue = $"app-embedding-{Guid.NewGuid():N}";
        var generator = new RecordingEmbeddingGenerator();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<EmbeddingWorkflow>()
                .AddAllActivities(new EmbeddingActivities(generator)));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (EmbeddingWorkflow workflow) => workflow.RunAsync("embed this"),
                new WorkflowOptions($"app-embedding-{Guid.NewGuid():N}", taskQueue));

            var vector = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(new[] { 0.25f, 0.75f }, vector);
            Assert.Equal("embed this", generator.Input);
            Assert.True(generator.ActivityTokenWasCancellable);
        });
    }

    [Workflow("TemporalCommunity.Extensions.AI.Tests.ApplicationOwnedEmbeddingWorkflow")]
    public sealed class EmbeddingWorkflow
    {
        [WorkflowRun]
        public Task<float[]> RunAsync(string text) =>
            Workflow.ExecuteActivityAsync(
                (EmbeddingActivities activities) => activities.GenerateAsync(text),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(2),
                    RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 },
                });
    }

    public sealed class EmbeddingActivities(
        IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        [Temporalio.Activities.Activity]
        public async Task<float[]> GenerateAsync(string text)
        {
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var embeddings = await generator.GenerateAsync(
                [text],
                cancellationToken: cancellationToken);
            return embeddings[0].Vector.ToArray();
        }
    }

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public string? Input { get; private set; }
        public bool ActivityTokenWasCancellable { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Input = Assert.Single(values);
            ActivityTokenWasCancellable = cancellationToken.CanBeCanceled;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 0.25f, 0.75f })]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
