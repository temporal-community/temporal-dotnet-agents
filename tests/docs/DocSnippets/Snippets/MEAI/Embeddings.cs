using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

namespace DocSnippets.MEAI;

internal static class Embeddings
{
    internal static void RegisterWorker(
        HostApplicationBuilder builder,
        string apiKey,
        string apiBaseUrl,
        string modelId,
        string taskQueue,
        string temporalAddress)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/embeddings.md#register-the-generator-and-activity
        IEmbeddingGenerator<string, Embedding<float>> generator = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) })
            .GetEmbeddingClient(modelId)
            .AsIEmbeddingGenerator();

        builder.Services.AddSingleton(generator);
        builder.Services
            .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
            .AddWorkflow<EmbeddingWorkflow>()
            .AddSingletonActivities<EmbeddingActivities>();
        // END SNIPPET docs/how-to/MEAI/embeddings.md#register-the-generator-and-activity
    }
}

// BEGIN SNIPPET docs/how-to/MEAI/embeddings.md#call-the-activity-from-workflow-code
[Workflow]
public sealed class EmbeddingWorkflow
{
    [WorkflowRun]
    public Task<float[]> RunAsync(string text) =>
        Workflow.ExecuteActivityAsync(
            (EmbeddingActivities activities) => activities.GenerateAsync(text),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(2),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
            });
}

public sealed class EmbeddingActivities(IEmbeddingGenerator<string, Embedding<float>> generator)
{
    [Temporalio.Activities.Activity]
    public async Task<float[]> GenerateAsync(string text)
    {
        var embeddings = await generator.GenerateAsync(
            [text],
            cancellationToken: Temporalio.Activities.ActivityExecutionContext.Current.CancellationToken);
        return embeddings[0].Vector.ToArray();
    }
}
// END SNIPPET docs/how-to/MEAI/embeddings.md#call-the-activity-from-workflow-code
