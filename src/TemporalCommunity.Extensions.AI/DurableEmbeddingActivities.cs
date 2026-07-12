using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Temporal activities that perform embedding generation.
/// The <see cref="IEmbeddingGenerator{String, Embedding}"/> is resolved from DI lazily at
/// activity invocation time — NOT injected via the constructor. This lets the activity
/// class be registered unconditionally by <see cref="DurableAIRegistrar"/> even when the
/// caller never registers an embedding generator (e.g., the DurableChat sample, which only
/// uses chat + tools). Eager constructor injection would fail under
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceProviderOptions.ValidateOnBuild"/>
/// (enabled by default in the Development environment by <c>Host.CreateApplicationBuilder</c>).
/// </summary>
internal sealed class DurableEmbeddingActivities(
    IServiceProvider services,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableEmbeddingActivities>();

    /// <summary>
    /// Generates embeddings by calling the inner generator. The generator is resolved from
    /// DI at invocation time; if no <see cref="IEmbeddingGenerator{String, Embedding}"/> is
    /// registered, the activity fails with a clear <see cref="InvalidOperationException"/>.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.AI.GenerateEmbedding")]
    public async Task<DurableEmbeddingOutput> GenerateAsync(DurableEmbeddingInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var generator = services.GetService<IEmbeddingGenerator<string, Embedding<float>>>()
            ?? throw new InvalidOperationException(
                "No IEmbeddingGenerator<string, Embedding<float>> is registered. Register one " +
                "in the service collection (e.g., via AddEmbeddingGenerator) before invoking " +
                "any embedding activity. The DurableEmbeddingActivities are registered " +
                "unconditionally by AddDurableAI, but the generator itself must be supplied " +
                "by the caller.");

        _logger.LogEmbeddingActivityStarted(input.Values.Count);

        ctx.Heartbeat();   // reset heartbeat timer before blocking on the embedding call
        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            embeddings = await generator.GenerateAsync(
                input.Values,
                input.Options,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Activity/workflow cancellation — never reclassify.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogEmbeddingActivityFailed(ex);

            if (Internal.LlmFailurePolicy.CreateNonRetryableFailure(ex) is { } nonRetryableFailure)
            {
                throw nonRetryableFailure;
            }

            throw;
        }

        _logger.LogEmbeddingActivityCompleted();

        return new DurableEmbeddingOutput { Embeddings = embeddings };
    }
}
