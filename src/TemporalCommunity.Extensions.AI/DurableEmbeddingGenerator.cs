using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingEmbeddingGenerator{String, Embedding}"/> middleware that wraps
/// embedding generation as a Temporal activity when running inside a workflow.
/// </summary>
/// <remarks>
/// Context-aware behavior:
/// <list type="bullet">
///   <item>Inside a Temporal workflow → dispatches via <c>Workflow.ExecuteActivityAsync</c></item>
///   <item>Otherwise → passes through to inner generator</item>
/// </list>
/// </remarks>
/// <param name="innerGenerator">The inner embedding generator to delegate to.</param>
/// <param name="durableOptions">Durable execution configuration.</param>
public sealed class DurableEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    DurableExecutionOptions durableOptions)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    // Field initializer validates durableOptions at construction time. ArgumentNullException.ThrowIfNull()
    // cannot be used here — primary constructors have no body for guard statements; field
    // initializers are the only available validation site.
    private readonly DurableExecutionOptions _durableOptions =
        durableOptions ?? throw new ArgumentNullException(nameof(durableOptions));

    /// <inheritdoc/>
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            return await base.GenerateAsync(values, options, cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as an activity.
        var input = new DurableEmbeddingInput
        {
            Values = values as IList<string> ?? values.ToList(),
            Options = options,
        };

        var activityOptions = CreateActivityOptions(options);

        // Keep this continuation on Temporal's workflow task scheduler. ConfigureAwait(false)
        // opts out of TaskScheduler.Current, so later workflow commands would no longer execute
        // through the active workflow context.
        var output = await Workflow.ExecuteActivityAsync(
            (DurableEmbeddingActivities a) => a.GenerateAsync(input),
            activityOptions);

        return output.Embeddings;
    }

    /// <summary>
    /// Creates the Temporal activity options used for a durable embedding request.
    /// </summary>
    internal ActivityOptions CreateActivityOptions(EmbeddingGenerationOptions? options) =>
        new()
        {
            TaskQueue = _durableOptions.TaskQueue,
            StartToCloseTimeout = _durableOptions.ActivityTimeout,
            HeartbeatTimeout = _durableOptions.HeartbeatTimeout,
            // A null policy would otherwise delegate to Temporal's unlimited server default.
            RetryPolicy = Internal.DefaultRetryPolicy.Resolve(_durableOptions.RetryPolicy),
            Summary = BuildActivitySummary(options),
        };

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the model id when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(EmbeddingGenerationOptions? options)
    {
        var modelId = options?.ModelId;
        return string.IsNullOrWhiteSpace(modelId) ? null : modelId;
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(DurableExecutionOptions) && serviceKey is null)
        {
            return _durableOptions;
        }

        return base.GetService(serviceType, serviceKey);
    }
}
