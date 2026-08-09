using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_PassesThroughWhenNotInWorkflow()
    {
        var expectedEmbeddings = new GeneratedEmbeddings<Embedding<float>>([
            new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })
        ]);

        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        A.CallTo(() => innerGenerator.GenerateAsync(
                A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(expectedEmbeddings));

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var generator = new DurableEmbeddingGenerator(innerGenerator, options);

        var result = await generator.GenerateAsync(["Hello world"]);

        Assert.Same(expectedEmbeddings, result);
        A.CallTo(() => innerGenerator.GenerateAsync(
                A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.Throws<ArgumentNullException>(
            () => new DurableEmbeddingGenerator(innerGenerator, null!));
    }

    [Fact]
    public void GetService_ReturnsDurableExecutionOptions()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var generator = new DurableEmbeddingGenerator(innerGenerator, options);

        var result = generator.GetService<DurableExecutionOptions>();
        Assert.Same(options, result);
    }

    [Fact]
    public void CreateActivityOptions_NullPolicy_UsesBoundedDefault()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var generator = new DurableEmbeddingGenerator(
            innerGenerator,
            new DurableExecutionOptions { TaskQueue = "test" });

        var activityOptions = generator.CreateActivityOptions(options: null);

        Assert.Equal("test", activityOptions.TaskQueue);
        Assert.NotNull(activityOptions.RetryPolicy);
        Assert.Equal(
            global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultMaximumAttempts,
            activityOptions.RetryPolicy.MaximumAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(
                global::TemporalCommunity.Extensions.AI.Internal.DefaultRetryPolicy.DefaultMaximumIntervalSeconds),
            activityOptions.RetryPolicy.MaximumInterval);
    }

    [Fact]
    public void CreateActivityOptions_ExplicitPolicy_IsPreserved()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var retryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 };
        var generator = new DurableEmbeddingGenerator(
            innerGenerator,
            new DurableExecutionOptions { TaskQueue = "test", RetryPolicy = retryPolicy });

        var activityOptions = generator.CreateActivityOptions(options: null);

        Assert.Same(retryPolicy, activityOptions.RetryPolicy);
    }

    [Fact]
    public void UseDurableExecution_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(
            () => EmbeddingGeneratorBuilderExtensions.UseDurableExecution(null!));
    }

    [Fact]
    public void UseDurableExecution_CreatesPipeline()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var builder = new EmbeddingGeneratorBuilder<string, Embedding<float>>(innerGenerator);

        builder.UseDurableExecution(opts => opts.TaskQueue = "emb-queue");
        var pipeline = builder.Build();

        var durableOptions = pipeline.GetService<DurableExecutionOptions>();
        Assert.NotNull(durableOptions);
        Assert.Equal("emb-queue", durableOptions!.TaskQueue);
    }

    // ── Activity Summary (visible in Temporal Web UI activity list) ────────

    [Fact]
    public void BuildActivitySummary_ReturnsModelId_WhenSet()
    {
        var opts = new EmbeddingGenerationOptions { ModelId = "text-embedding-3-small" };
        Assert.Equal("text-embedding-3-small", DurableEmbeddingGenerator.BuildActivitySummary(opts));
    }

    [Fact]
    public void BuildActivitySummary_ReturnsNull_WhenOptionsNull() =>
        Assert.Null(DurableEmbeddingGenerator.BuildActivitySummary(null));

    [Fact]
    public void BuildActivitySummary_ReturnsNull_WhenModelIdMissing()
    {
        Assert.Null(DurableEmbeddingGenerator.BuildActivitySummary(new EmbeddingGenerationOptions()));
        Assert.Null(DurableEmbeddingGenerator.BuildActivitySummary(new EmbeddingGenerationOptions { ModelId = "" }));
        Assert.Null(DurableEmbeddingGenerator.BuildActivitySummary(new EmbeddingGenerationOptions { ModelId = "   " }));
    }

    [Fact]
    public void DurableEmbeddingActivities_Constructor_AcceptsNullLogger()
    {
        // DurableEmbeddingActivities resolves the IEmbeddingGenerator lazily from IServiceProvider
        // at activity invocation time (so the type can be DI-registered unconditionally without
        // requiring the caller to register an embedding generator). Constructor only needs an
        // IServiceProvider — empty one is fine for this construction test.
        var services = new ServiceCollection().BuildServiceProvider();
        var activities = new DurableEmbeddingActivities(services, null);
        Assert.NotNull(activities);
    }
}
