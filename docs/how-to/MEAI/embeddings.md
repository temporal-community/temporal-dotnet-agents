# Durable Embeddings

`DurableEmbeddingGenerator` is a `DelegatingEmbeddingGenerator` middleware that makes `IEmbeddingGenerator` calls durable. When called inside a Temporal workflow it dispatches to `DurableEmbeddingActivities.GenerateAsync` as an activity — automatically retried on failure and never re-executed after completion. Outside a workflow it passes through to the inner generator unchanged.

## Registration

Chain `UseDurableExecution()` on the embedding generator builder, then call `AddDurableAI()` on the worker to register the backing activity class.

```csharp
// Worker
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OpenAIEmbeddingGenerator(openAiClient, "text-embedding-3-small")
        .AsBuilder()
        .UseDurableExecution(opts =>
        {
            opts.ActivityTimeout  = TimeSpan.FromMinutes(2);
            opts.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        })
        .Build());

builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI();
```

`AddDurableAI()` registers `DurableEmbeddingActivities` on the worker. Without it, calls inside a workflow will fail to find the activity.

## Calling GenerateAsync Inside a Workflow

Resolve `IEmbeddingGenerator` from DI inside your workflow's activity or call it from a custom workflow. The middleware detects `Workflow.InWorkflow` and dispatches accordingly.

```csharp
// Workflow
[Workflow]
public class DocumentIndexingWorkflow
{
    [WorkflowRun]
    public async Task<float[]> RunAsync(string text)
    {
        // Dispatched as DurableEmbeddingActivities.GenerateAsync when inside a workflow.
        var embeddings = await _generator.GenerateAsync([text]);
        return embeddings[0].Vector.ToArray();
    }
}
```

`IEmbeddingGenerator` must be injected into the workflow's activity or resolved at runtime — it cannot be constructor-injected directly into a `[Workflow]` class (workflows must be deterministic; DI resolution at construction time is not safe).

## Configuration

All options come from `DurableExecutionOptions` set during `UseDurableExecution()`:

| Option | Default | Description |
|--------|---------|-------------|
| `ActivityTimeout` | From `DurableExecutionOptions` | Start-to-close timeout for the embedding activity |
| `HeartbeatTimeout` | From `DurableExecutionOptions` | Heartbeat timeout; set to detect stalled embedding calls |
| `RetryPolicy` | Bounded default (5 attempts; 2-second maximum backoff) | Override to limit or tune retries on transient provider errors |

When the embedding provider returns a deterministic HTTP failure (`400`, `401`, `403`, `404`, or
`422`), the activity fails immediately rather than retrying. Other errors use the configured policy
or the bounded default above, so an unknown permanent failure cannot retry forever.

```csharp
// Worker
.UseDurableExecution(opts =>
{
    opts.ActivityTimeout = TimeSpan.FromMinutes(2);
    opts.RetryPolicy     = new RetryPolicy { MaximumAttempts = 3 };
})
```

## Runnable Example

```bash
temporal server start-dev
dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```

See `samples/MEAI/DurableEmbeddings/` for a complete parallel fan-out example that generates embeddings for multiple documents concurrently inside a workflow.
