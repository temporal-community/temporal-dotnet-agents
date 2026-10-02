// Harness parameters supply application configuration omitted from the registration examples.
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.AI;

namespace DocSnippets.MEAI;

internal static class Embeddings
{
    internal static void RegisterProvider(
        HostApplicationBuilder builder, string apiKey, string apiBaseUrl, string modelId)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/embeddings.md#register-the-provider-on-the-activity-worker
        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

        builder.Services
            .AddEmbeddingGenerator(
                openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator())
            .Build();
        // END SNIPPET docs/how-to/MEAI/embeddings.md#register-the-provider-on-the-activity-worker
    }

    internal static void RegisterWorkers(
        HostApplicationBuilder builder, string activityTaskQueue, string workflowTaskQueue)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/embeddings.md#register-the-provider-on-the-activity-worker
        builder.Services
            .AddHostedTemporalWorker(activityTaskQueue)
            .AddDurableAI(options =>
            {
                options.TaskQueue = activityTaskQueue;
                options.ActivityTimeout = TimeSpan.FromMinutes(2);
                options.RegisterDefaultWorkflow = false;
            });

        builder.Services
            .AddHostedTemporalWorker(workflowTaskQueue)
            .AddWorkflow<DocumentIndexingWorkflow>();
        // END SNIPPET docs/how-to/MEAI/embeddings.md#register-the-provider-on-the-activity-worker
    }
}

// BEGIN SNIPPET docs/how-to/MEAI/embeddings.md#construct-the-adapter-inside-workflow-code
[Workflow]
public sealed class DocumentIndexingWorkflow
{
    [WorkflowRun]
    public async Task<float[]> RunAsync(DocumentIndexingInput input)
    {
        var generator = new DurableEmbeddingGenerator(
            new NullEmbeddingGenerator(),
            new DurableExecutionOptions
            {
                TaskQueue = input.ActivityTaskQueue,
                ActivityTimeout = input.ActivityTimeout,
                RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
            });

        var embeddings = await generator.GenerateAsync(
            [input.Chunks[0]],
            new EmbeddingGenerationOptions { ModelId = input.ModelId });

        return embeddings[0].Vector.ToArray();
    }
}
// END SNIPPET docs/how-to/MEAI/embeddings.md#construct-the-adapter-inside-workflow-code

// The guide explicitly delegates these application-local types to the runnable sample.
public sealed class DocumentIndexingInput
{
    public required IReadOnlyList<string> Chunks { get; init; }
    public required string ActivityTaskQueue { get; init; }
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public required string ModelId { get; init; }
}

internal sealed class NullEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The workflow must dispatch to the embedding activity.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
