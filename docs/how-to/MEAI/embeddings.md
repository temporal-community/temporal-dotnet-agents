# Durable Embeddings with an Application-Owned Activity

The MEAI package does not provide a durable embedding-generator wrapper. To make embedding
generation durable, call an application-owned Temporal activity from your workflow and inject
`IEmbeddingGenerator<string, Embedding<float>>` into that activity. Temporal records the activity
input and result in workflow history; completed results replay without calling the provider again.
An incomplete activity attempt can run again according to the retry policy.

## Register the generator and activity

This example uses the public adapter from `Microsoft.Extensions.AI.OpenAI` and registers a normal
activity. Keep provider credentials and the generator in worker-side dependency injection; do not
pass either through workflow inputs.

```csharp
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
```

## Call the activity from workflow code

Configure an explicit start-to-close timeout and bounded retry policy for the provider call. The
activity forwards Temporal's cancellation token to MEAI, allowing the provider request to stop when
the activity is cancelled.

```csharp
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
```

## Payload size, privacy, and retries

Activity arguments and results are persisted in Temporal history. Large vectors can make histories
and payloads expensive; sensitive vectors become part of the history data path and are subject to
the application's Temporal access controls, codecs, and retention. For large or sensitive vectors,
write them to application-controlled external storage and return a bounded reference instead.
External writes should be idempotent because retries can repeat an incomplete activity attempt.

## Runnable example

The [DurableEmbeddings sample](../../../samples/MEAI/DurableEmbeddings/) shows this pattern with an
application-owned activity, an explicit timeout and retry policy, and cancellation-token forwarding.
Start Temporal Service 1.31.0 or newer, set the provider credentials, and run:

```bash
temporal server start-dev
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableEmbeddings
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```
