using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("Set OPENAI_API_KEY in user secrets or the environment.");
if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("Set OPENAI_API_BASE_URL in user secrets or the environment.");

const string taskQueue = "application-embeddings";
var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress));

IEmbeddingGenerator<string, Embedding<float>> generator = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) })
    .GetEmbeddingClient(model)
    .AsIEmbeddingGenerator();

builder.Services.AddSingleton(generator);
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddWorkflow<EmbeddingWorkflow>()
    .AddSingletonActivities<EmbeddingActivities>();

var host = builder.Build();
await host.StartAsync();

var handle = await client.StartWorkflowAsync(
    (EmbeddingWorkflow workflow) => workflow.RunAsync("Temporal makes workflows durable."),
    new WorkflowOptions($"embedding-{Guid.NewGuid():N}", taskQueue));
var vector = await handle.GetResultAsync();

Console.WriteLine($"Generated embedding with {vector.Length} dimensions.");
await host.StopAsync();

[Temporalio.Workflows.Workflow]
public sealed class EmbeddingWorkflow
{
    [Temporalio.Workflows.WorkflowRun]
    public Task<float[]> RunAsync(string text) =>
        Temporalio.Workflows.Workflow.ExecuteActivityAsync(
            (EmbeddingActivities activities) => activities.GenerateAsync(text),
            new Temporalio.Workflows.ActivityOptions
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
