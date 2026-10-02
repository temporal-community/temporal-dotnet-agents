# Durable Embeddings

`DurableEmbeddingGenerator` wraps an `IEmbeddingGenerator<string, Embedding<float>>` and
dispatches each workflow-side `GenerateAsync` call as a `DurableEmbeddingActivities.GenerateAsync`
activity. The activity resolves the real provider-backed generator from worker DI. Completed
activity results replay from Temporal history; an incomplete attempt may run again according to
its retry policy.

## Register the provider on the activity worker

Use the public MEAI adapter from the pinned `Microsoft.Extensions.AI.OpenAI` 10.8.3 package. The
`OpenAIEmbeddingGenerator` implementation type is not the API to construct directly; adapt the
public OpenAI embedding client with `AsIEmbeddingGenerator()`:

```csharp
var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

builder.Services
    .AddEmbeddingGenerator(
        openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator())
    .Build();
```

Register `AddDurableAI()` on a worker that polls the activity queue. The workflow worker may use a
different queue:

```csharp
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
```

`AddDurableAI()` registers `DurableEmbeddingActivities`. Its worker-side activity resolves the
real `IEmbeddingGenerator<string, Embedding<float>>` from DI and calls the provider. If workflow and
activity workers use different queues, the activity queue must be set in the durable options and
carried in the workflow input, as in the [runnable sample](../../../samples/MEAI/DurableEmbeddings/).

## Construct the adapter inside workflow code

Do not inject an `IEmbeddingGenerator` or resolve application DI services in a workflow. Construct
the durable wrapper from deterministic workflow input instead. Its inner generator satisfies the
constructor, but is not called when `Workflow.InWorkflow` is true: the wrapper schedules the
activity on `DurableExecutionOptions.TaskQueue`.

```csharp
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
```

`DocumentIndexingInput` is an application DTO, and `NullEmbeddingGenerator` in this example is a
sample-local throwing stub, not a library type. See
[`DocumentIndexingWorkflow.cs`](../../../samples/MEAI/DurableEmbeddings/DocumentIndexingWorkflow.cs)
for its implementation and the full sequential/parallel examples. The task-queue value originates
in the workflow input; `DurableExecutionOptions.TaskQueue` routes the embedding activity to that
queue, where the real provider generator is registered.

## Configuration

The workflow-side adapter takes `DurableExecutionOptions`:

| Option | Purpose |
|--------|---------|
| `TaskQueue` | Queue polled by the worker hosting `DurableEmbeddingActivities`; pass it in workflow input when the queues differ. |
| `ActivityTimeout` | Start-to-close timeout for each embedding activity. |
| `HeartbeatTimeout` | Heartbeat timeout for the activity; choose it for the provider's expected behavior and workload. |
| `RetryPolicy` | Retry policy for an incomplete/failed activity. When omitted, the library uses a bounded five-attempt default; set an explicit policy for the operation's idempotency and failure budget. |

Each activity is independent. Calling `GenerateAsync([chunk])` once per chunk provides one retry
boundary per chunk. To fan out in a workflow, collect the tasks and await them with
`Workflow.WhenAllAsync` rather than `Task.WhenAll`.

## Runnable example

Start Temporal Service 1.31.0 or newer, then configure credentials and run the sample:

```bash
temporal server start-dev
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```

In PowerShell:

```powershell
temporal server start-dev
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```
