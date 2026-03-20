// ParallelDocumentIndexingWorkflow.cs
//
// A parallel variant of DocumentIndexingWorkflow that fans out all embedding
// activities concurrently using Workflow.WhenAllAsync, rather than awaiting
// each one in sequence.
//
// HOW PARALLEL DISPATCH WORKS
// ────────────────────────────
// Inside a Temporal workflow, DurableEmbeddingGenerator.GenerateAsync dispatches
// to Workflow.ExecuteActivityAsync, which returns a Task. The Task is created
// immediately — before any await. Collecting all Tasks via LINQ and passing them
// to Workflow.WhenAllAsync causes Temporal to schedule every activity at the same
// time, letting the worker (and the external API) process them concurrently rather
// than waiting for each to finish before starting the next.
//
// WHY Workflow.WhenAllAsync AND NOT Task.WhenAll
// ───────────────────────────────────────────────
// Temporal .NET workflows use a custom TaskScheduler. Task.WhenAll bypasses this
// scheduler, which breaks determinism during history replay — Temporal may fail
// with a non-determinism error on worker restart. Workflow.WhenAllAsync is the
// workflow-safe equivalent that works correctly with Temporal's replay mechanism.
//
// SEQUENTIAL vs PARALLEL
// ──────────────────────
//  Sequential (DocumentIndexingWorkflow):
//    embed chunk[0] → wait → embed chunk[1] → wait → ... → embed chunk[N-1]
//    Elapsed ≈ N × per-activity-time
//
//  Parallel (ParallelDocumentIndexingWorkflow):
//    emit all N activities at once → wait for all to complete
//    Elapsed ≈ max(per-activity-time) — often much faster for large batches
//
// DURABILITY GUARANTEES ARE PRESERVED
// ─────────────────────────────────────
// Even with parallel dispatch, Temporal's durability contract is fully intact:
//   • Each embedding activity is independently retried on transient failure.
//   • If the worker crashes mid-run, completed activities replay from history;
//     only in-flight activities are re-scheduled.
//   • The result order matches the input chunk order (task list preserves order).

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace DurableEmbeddings;

/// <summary>
/// Result of a parallel document-indexing run.
/// </summary>
public sealed class ParallelIndexingResult
{
    /// <summary>Total number of chunks that were indexed.</summary>
    public required int ChunksProcessed { get; init; }

    /// <summary>The number of dimensions in each embedding vector.</summary>
    public required int Dimensions { get; init; }
}

/// <summary>
/// A Temporal workflow that indexes a batch of text chunks by generating all
/// embeddings concurrently — each chunk is dispatched as a separate Temporal
/// activity, and all activities are scheduled in parallel using
/// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same <see cref="DocumentIndexingInput"/> as the sequential variant
/// so that both workflows can be driven with identical inputs for comparison.
/// </para>
/// <para>
/// The parallel dispatch is achieved by collecting the <see cref="Task{T}"/>
/// objects returned by <see cref="DurableEmbeddingGenerator.GenerateAsync"/> before
/// awaiting them. Inside a workflow, each such Task wraps a
/// <c>Workflow.ExecuteActivityAsync</c> call, so collecting N tasks schedules
/// N activities concurrently. <c>Workflow.WhenAllAsync</c> (not <c>Task.WhenAll</c>)
/// then waits for all to complete in a replay-safe manner.
/// </para>
/// </remarks>
[Workflow]
public sealed class ParallelDocumentIndexingWorkflow
{
    [WorkflowRun]
    public async Task<ParallelIndexingResult> RunAsync(DocumentIndexingInput input)
    {
        var embeddingOptions = new DurableExecutionOptions
        {
            ActivityTimeout = input.ActivityTimeout,
        };

        // The NullEmbeddingGenerator is never invoked: Workflow.InWorkflow == true
        // causes DurableEmbeddingGenerator to dispatch to DurableEmbeddingActivities
        // on every GenerateAsync call.
        var generator = new DurableEmbeddingGenerator(new NullEmbeddingGenerator(), embeddingOptions);

        // ── Fan-out: start all embedding activities at the same time ────────────
        //
        // Each generator.GenerateAsync call immediately schedules a
        // Workflow.ExecuteActivityAsync and returns a running Task.
        // We materialize every Task into a list (.ToList()) before any await,
        // so Temporal sees all N activity commands at once and schedules them
        // in parallel rather than sequentially.
        var tasks = input.Chunks
            .Select(chunk => generator.GenerateAsync([chunk]))
            .ToList();

        // ── Fan-in: wait for all activities using the workflow-safe combinator ──
        //
        // Workflow.WhenAllAsync is the correct replacement for Task.WhenAll inside
        // a [Workflow] class. Task.WhenAll bypasses Temporal's custom TaskScheduler
        // and breaks determinism during history replay; Workflow.WhenAllAsync works
        // correctly with the SDK's replay mechanism.
        //
        // Results are returned in the same order as input.Chunks.
        var generatedEmbeddings = await Workflow.WhenAllAsync(tasks);

        var dimensions = generatedEmbeddings.Length > 0
            ? generatedEmbeddings[0][0].Vector.Length
            : 0;

        return new ParallelIndexingResult
        {
            ChunksProcessed = generatedEmbeddings.Length,
            Dimensions = dimensions,
        };
    }
}
