# DurableEmbeddings: Fault-Tolerant RAG Indexing

## Overview

This sample demonstrates direct `DurableEmbeddingGenerator` middleware, where each `GenerateAsync`
call dispatches as a separate Temporal activity. Two workflow variants show
sequential and parallel fan-out strategies for indexing a document corpus. If the worker crashes
mid-batch, completed embeddings replay from workflow history — no API calls are repeated.

- `DurableEmbeddingGenerator` — middleware that detects workflow context and routes to the
  configured activity task queue
- `DocumentIndexingWorkflow` — sequential per-chunk embedding; one activity per chunk
- `ParallelDocumentIndexingWorkflow` — concurrent fan-out via `Workflow.WhenAllAsync`
- Crash recovery: completed activities replay from history; only remaining chunks are re-run
- `DurableEmbeddingActivities` is registered automatically by `AddDurableAI()` (when used with `AddHostedTemporalWorker(...)`)
- Separate task queues: workflow workers poll `durable-embeddings-workflows`; the direct adapter
  routes embedding activities to `durable-embeddings-activities` through
  `DurableExecutionOptions.TaskQueue`

## Architecture

```
Sequential                           Parallel
──────────                           ────────
DocumentIndexingWorkflow             ParallelDocumentIndexingWorkflow
  foreach chunk                        tasks = chunks.Select(GenerateAsync).ToList()
    await generator.GenerateAsync()    await Workflow.WhenAllAsync(tasks)
      └─ DurableEmbeddingActivities      └─ N concurrent DurableEmbeddingActivities
           └─ IEmbeddingGenerator             └─ IEmbeddingGenerator (per activity)
                └─ OpenAI API                      └─ OpenAI API
```

The sample deliberately registers two workers. The workflows start on
`durable-embeddings-workflows`, while the worker containing `DurableEmbeddingActivities` polls
`durable-embeddings-activities`. Each workflow input carries the activity queue into
`DurableExecutionOptions.TaskQueue`; `DurableEmbeddingGenerator` assigns that value to the
scheduled `ActivityOptions.TaskQueue`.

## Highlights

- **One activity per chunk, not one per batch.** This gives independent retry granularity: if chunk 3 fails on a rate-limit error, only chunk 3 is retried. Chunks 1 and 2 are replayed from history — no wasted API calls.
- **`Workflow.WhenAllAsync`, not `Task.WhenAll`.** Inside a `[Workflow]` class, `Task.WhenAll` bypasses Temporal's custom `TaskScheduler` and breaks determinism during history replay. `Workflow.WhenAllAsync` is the correct replacement.
- **`NullEmbeddingGenerator` as a workflow-side stub.** `DurableEmbeddingGenerator` requires an inner generator in its constructor, but `Workflow.InWorkflow == true` prevents it from ever being called. A lightweight `NullEmbeddingGenerator` satisfies the constructor without pulling in API credentials on the workflow thread.
- **Parallel wall-clock time approaches `max(per-activity)` not `sum`.** The parallel demo schedules all N activities in one Temporal scheduling round, so total elapsed time scales with the slowest chunk rather than all chunks combined.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)
- An OpenAI-compatible API key (`OPENAI_API_KEY`)
- Optional: `OPENAI_API_BASE_URL` (defaults to `https://api.openai.com/v1`) and `OPENAI_EMBEDDING_MODEL` (defaults to `text-embedding-3-small`)

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_EMBEDDING_MODEL" "text-embedding-3-small" --project samples/MEAI/DurableEmbeddings
```

### Run

```bash
dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```

### Expected Output

```
 Demo: Durable Document Indexing (RAG embedding pipeline)
   Chunks to index: 3
   Elapsed         : ~Nms (sequential, varies)
   Chunks indexed  : 3
   Vector dimension: 1536
   Similarity (chunk 1 vs 2): 0.3241  (varies; lower = more distinct)

 Demo: Parallel Document Indexing (fan-out embedding)
   Chunks to index: 5
   Elapsed          : ~Nms (parallel, varies; approaches max(per-activity))
   Chunks processed : 5
   Vector dimension : 1536
```
