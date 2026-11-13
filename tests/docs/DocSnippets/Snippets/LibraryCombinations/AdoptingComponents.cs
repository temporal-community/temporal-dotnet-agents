// HARNESS for docs/library-combinations.md § "Adopt components in a custom workflow".
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;
using DocSnippets.MEAI;

namespace DocSnippets.LibraryCombinations;

internal static class AdoptingComponents
{
    internal static async Task ConfigureAsync(
        HostApplicationBuilder builder,
        string taskQueue,
        IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        // BEGIN SNIPPET docs/library-combinations.md#adopt-components-in-a-custom-workflow
        // Application-owned: HostApplicationBuilder builder, taskQueue, and generator.
        // EmbeddingWorkflow and EmbeddingActivities are application-defined types.
        builder.Services.AddTemporalClient("localhost:7233", "default");
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<EmbeddingWorkflow>()
            .AddSingletonActivities<EmbeddingActivities>();
        // END SNIPPET docs/library-combinations.md#adopt-components-in-a-custom-workflow
    }
}
