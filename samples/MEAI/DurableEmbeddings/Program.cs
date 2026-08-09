// DurableEmbeddings — demonstrates wrapping each GenerateAsync call as an independent
// Temporal activity for fault-tolerant RAG indexing. The workflows construct
// DurableEmbeddingGenerator directly (the durable-wrapper short-circuit fires on
// Workflow.InWorkflow == true, so the inner generator is never invoked inside a workflow);
// on the worker side, DurableEmbeddingActivities resolves the real IEmbeddingGenerator
// from DI. Includes sequential and parallel fan-out workflow variants.
//
// Run:  dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj

using System.ClientModel;
using System.Diagnostics;
using DurableEmbeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL") ?? "https://api.openai.com/v1";
var embeddingModel = builder.Configuration.GetValue<string>("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/DurableEmbeddings");

const string workflowTaskQueue = "durable-embeddings-workflows";
const string activityTaskQueue = "durable-embeddings-activities";

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which correctly handles MEAI's $type discriminator
// for polymorphic AIContent subclasses. Required whenever MEAI AIContent polymorphism
// crosses Temporal serialization boundaries. Strictly speaking, an embeddings-only
// sample doesn't need it (Embedding<float> isn't polymorphic), but setting it is
// harmless and matches the recommended pattern for any MEAI + Temporal app.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Create the OpenAI client ──────────────────────────────────────────
var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Setup: Register IEmbeddingGenerator ──────────────────────────────────────
// AddEmbeddingGenerator is the idiomatic MEAI DI pattern — it returns an
// EmbeddingGeneratorBuilder, and Build() registers the final
// IEmbeddingGenerator<string, Embedding<float>> singleton.
//
// We do NOT chain .UseDurableExecution() here. The workflows in this sample
// construct DurableEmbeddingGenerator directly (the durable-wrapper short-circuits
// on Workflow.InWorkflow == true, so wrapping the DI pipeline as well would be
// dead code). On the worker side, DurableEmbeddingActivities resolves this
// inner IEmbeddingGenerator from DI and calls GenerateAsync — that is what
// actually reaches the OpenAI API.
builder.Services
    .AddEmbeddingGenerator(
        openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator())
    .Build();

// ── Setup: Register separate workflow and AI-activity workers ────────────────
// AddDurableAI registers options, the DurableAIDataConverter auto-wire, internal
// activities (DurableChatActivities, DurableEmbeddingActivities, DurableFunctionActivities),
// and the tool/function registry. Only DurableEmbeddingActivities is exercised here.
//
// RegisterDefaultWorkflow = false suppresses the DurableChatWorkflow + DurableChatSessionClient
// registrations (we don't need chat-session machinery for an embeddings-only sample). With
// the default workflow disabled, we also do not need to register a dummy IChatClient — the
// DurableMixedPatternValidator tolerates a missing unkeyed IChatClient when no durable tools
// are registered, and DurableChatActivities resolves IChatClient lazily (never invoked here).
//
// The activity worker polls activityTaskQueue. The workflow worker polls workflowTaskQueue;
// DurableEmbeddingGenerator explicitly routes each activity to activityTaskQueue.
builder.Services
    .AddHostedTemporalWorker(activityTaskQueue)
    .AddDurableAI(opts =>
    {
        opts.TaskQueue = activityTaskQueue;
        opts.ActivityTimeout = TimeSpan.FromMinutes(2);
        opts.RegisterDefaultWorkflow = false;
    });
builder.Services
    .AddHostedTemporalWorker(workflowTaskQueue)
    .AddWorkflow<DocumentIndexingWorkflow>()
    .AddWorkflow<ParallelDocumentIndexingWorkflow>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demos ─────────────────────────────────────────────────────────────────
await RunDocumentIndexingDemoAsync(
    temporalClient,
    workflowTaskQueue,
    activityTaskQueue,
    embeddingModel);
await RunParallelIndexingDemoAsync(
    temporalClient,
    workflowTaskQueue,
    activityTaskQueue,
    embeddingModel);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ═════════════════════════════════════════════════════════════════════════════
// Demo: DocumentIndexingWorkflow — durable embedding generation per chunk
//
// Each text chunk is embedded as a separate Temporal activity. The workflow
// returns the vector dimension and the dot-product similarity between the
// first two chunks, proving they have distinct semantic representations.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunDocumentIndexingDemoAsync(
    ITemporalClient client,
    string workflowTaskQueue,
    string activityTaskQueue,
    string modelId)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Durable Document Indexing (RAG embedding pipeline)");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Sample text chunks representing paragraphs from a document.
    // In a real RAG pipeline these would come from a PDF, web page, or database row.
    var chunks = new[]
    {
        "Temporal is a durable execution platform that automatically retries failed " +
            "activities and replays workflow history on worker restart.",

        "The Eiffel Tower is a wrought-iron lattice tower on the Champ de Mars in Paris, " +
            "France, built between 1887 and 1889 as the centerpiece of the 1889 World's Fair.",

        "Microsoft Extensions AI (MEAI) provides a unified abstraction layer for " +
            "large language models, embedding generators, and AI middleware in .NET.",
    };

    Console.WriteLine($" Chunks to index: {chunks.Length}");
    for (int i = 0; i < chunks.Length; i++)
    {
        Console.WriteLine($"   [{i + 1}] {chunks[i][..Math.Min(70, chunks[i].Length)]}...");
    }
    Console.WriteLine();

    var workflowId = $"doc-index-{Guid.NewGuid():N}";
    Console.WriteLine($" Workflow ID: {workflowId}");
    Console.WriteLine(" Starting DocumentIndexingWorkflow...\n");

    var sw = Stopwatch.StartNew();

    // Execute the workflow. Each chunk becomes one DurableEmbeddingActivities invocation.
    // If this process crashes mid-run, Temporal will replay completed embeddings from
    // history and only re-run the remaining chunks — no wasted API calls.
    var result = await client.ExecuteWorkflowAsync(
        (DocumentIndexingWorkflow wf) => wf.RunAsync(new DocumentIndexingInput
        {
            Chunks = chunks,
            ActivityTaskQueue = activityTaskQueue,
            ActivityTimeout = TimeSpan.FromMinutes(2),
            ModelId = modelId,
        }),
        new WorkflowOptions
        {
            Id = workflowId,
            TaskQueue = workflowTaskQueue,
        });

    sw.Stop();

    Console.WriteLine(" Results:");
    Console.WriteLine($"   Elapsed         : {sw.ElapsedMilliseconds} ms (sequential)");
    Console.WriteLine($"   Chunks indexed  : {result.Chunks.Count}");
    Console.WriteLine($"   Vector dimension: {result.Dimensions}");

    if (result.FirstPairSimilarity.HasValue)
    {
        // Dot-product similarity between chunk 1 (Temporal) and chunk 2 (Eiffel Tower).
        // A higher value means more similar semantics. These topics are unrelated, so
        // the similarity should be noticeably lower than comparing two on-topic chunks.
        Console.WriteLine($"   Similarity (chunk 1 vs 2): {result.FirstPairSimilarity.Value:F4}");
        Console.WriteLine("   (dot-product of unit-normalised OpenAI embeddings;");
        Console.WriteLine("    lower value = more distinct semantic content)");
    }

    Console.WriteLine();
    Console.WriteLine(" Each embedding was generated as a separate Temporal activity:");
    Console.WriteLine("   • Independently retried on transient failures (rate limits, timeouts)");
    Console.WriteLine("   • Completed embeddings replay from history on worker restart");
    Console.WriteLine("   • Visible individually in the Temporal UI for progress tracking");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo: ParallelDocumentIndexingWorkflow — concurrent fan-out embedding
//
// All embedding activities are scheduled at the same time via Workflow.WhenAllAsync.
// Temporal orchestrates concurrent execution: the worker dispatches every chunk
// in a single scheduling round rather than waiting for each to complete before
// starting the next. Contrast with the sequential demo above.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunParallelIndexingDemoAsync(
    ITemporalClient client,
    string workflowTaskQueue,
    string activityTaskQueue,
    string modelId)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Parallel Document Indexing (fan-out embedding)");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Five thematically connected paragraphs from the same domain — used to
    // show that all five embeddings are generated in a single parallel round,
    // each as its own independently retried Temporal activity.
    var chunks = new[]
    {
        "Temporal is a durable execution platform that automatically retries failed " +
            "activities and replays workflow history on worker restart, providing " +
            "fault-tolerant orchestration without manual retry logic.",

        "A Temporal workflow is written as ordinary async C# code. The SDK serialises " +
            "every completed step into an event history so the workflow can be resumed " +
            "on any worker after a crash without losing progress.",

        "Activities are the units of work in Temporal: they run outside the workflow " +
            "sandbox, can call external APIs and databases, and are individually retried " +
            "according to a configurable retry policy.",

        "The Temporal server persists the complete event history for every workflow " +
            "execution. Workers replay this history to reconstruct in-memory state, " +
            "guaranteeing exactly-once semantics for completed activity results.",

        "Workflow versioning lets you deploy new code alongside in-flight executions. " +
            "The patching API inserts conditional branches so existing histories " +
            "continue down the old path while new executions take the new path.",
    };

    Console.WriteLine($" Chunks to index: {chunks.Length}");
    for (int i = 0; i < chunks.Length; i++)
    {
        Console.WriteLine($"   [{i + 1}] {chunks[i][..Math.Min(70, chunks[i].Length)]}...");
    }
    Console.WriteLine();

    var workflowId = $"doc-index-parallel-{Guid.NewGuid():N}";
    Console.WriteLine($" Workflow ID: {workflowId}");
    Console.WriteLine(" Starting ParallelDocumentIndexingWorkflow...\n");

    var sw = Stopwatch.StartNew();

    // All N embedding activities are dispatched concurrently.
    // Workflow.WhenAllAsync (the workflow-safe replacement for Task.WhenAll)
    // waits for all of them before the workflow returns — preserving correct
    // history replay behaviour on worker restart.
    var result = await client.ExecuteWorkflowAsync(
        (ParallelDocumentIndexingWorkflow wf) => wf.RunAsync(new DocumentIndexingInput
        {
            Chunks = chunks,
            ActivityTaskQueue = activityTaskQueue,
            ActivityTimeout = TimeSpan.FromMinutes(2),
            ModelId = modelId,
        }),
        new WorkflowOptions
        {
            Id = workflowId,
            TaskQueue = workflowTaskQueue,
        });

    sw.Stop();

    Console.WriteLine(" Results:");
    Console.WriteLine($"   Elapsed          : {sw.ElapsedMilliseconds} ms (parallel)");
    Console.WriteLine($"   Chunks processed : {result.ChunksProcessed}");
    Console.WriteLine($"   Vector dimension : {result.Dimensions}");
    Console.WriteLine();
    Console.WriteLine(" All embeddings were dispatched concurrently as Temporal activities:");
    Console.WriteLine("   • Workflow.WhenAllAsync scheduled all N activities in one round");
    Console.WriteLine("   • Each activity is still independently retried on failure");
    Console.WriteLine("   • Completed activities replay from history on worker restart");
    Console.WriteLine("   • Compare elapsed time with the sequential demo above —");
    Console.WriteLine("     wall-clock time approaches max(per-activity) rather than sum");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}
