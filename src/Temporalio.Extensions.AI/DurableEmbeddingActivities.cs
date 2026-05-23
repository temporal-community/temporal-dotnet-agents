using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace Temporalio.Extensions.AI;

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
    [Activity("Temporalio.Extensions.AI.GenerateEmbedding")]
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

        _logger.LogDebug("Executing durable embedding activity for {Count} inputs", input.Values.Count);

        ctx.Heartbeat();   // reset heartbeat timer before blocking on the embedding call
        var embeddings = await generator.GenerateAsync(
            input.Values,
            input.Options,
            ct).ConfigureAwait(false);

        _logger.LogDebug("Durable embedding activity completed");

        return new DurableEmbeddingOutput { Embeddings = embeddings };
    }
}
