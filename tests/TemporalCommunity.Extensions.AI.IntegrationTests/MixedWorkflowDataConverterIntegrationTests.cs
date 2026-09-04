using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

public class MixedWorkflowDataConverterIntegrationTests
{
    [Fact]
    public async Task SharedWorker_OrdinaryWorkflowNestedResult_RoundTripsWithConfiguredManualCaller()
    {
        await using var environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
        var taskQueue = $"mixed-converter-{Guid.NewGuid():N}";
        var targetHost = environment.Client.Connection.Options.TargetHost
            ?? throw new InvalidOperationException("Test server target host is unavailable.");

        var caller = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(targetHost)
        {
            Namespace = environment.Client.Options.Namespace,
            DataConverter = DurableAIDataConverter.Instance,
        });
        var incompatibleCaller = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(targetHost)
            {
                Namespace = environment.Client.Options.Namespace,
            });

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient>(new TestChatClient());

        // Keep this ordering intentional: an ordinary workflow can share a worker that is
        // subsequently enabled for durable AI. The worker creates its own Temporal client,
        // so AddDurableAI's converter plugin is the configuration path under test.
        var worker = builder.Services
            .AddHostedTemporalWorker(targetHost, environment.Client.Options.Namespace, taskQueue)
            .AddWorkflow<SharedWorkerStatusWorkflow>();
        worker.AddDurableAI(options => options.RegisterDefaultWorkflow = false);

        using var host = builder.Build();
        Assert.Null(host.Services.GetService<ITemporalClient>());
        await host.StartAsync();
        try
        {
            var handle = await caller.StartWorkflowAsync(
                (SharedWorkerStatusWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions($"mixed-converter-{Guid.NewGuid():N}", taskQueue));

            var result = await handle.GetResultAsync<SharedWorkerResult>()
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(1, result.Migration.AppliedCount);
            Assert.Equal("schema-created", result.Migration.Message);
            Assert.Equal(6, result.Seed.CategoriesSeeded);
            Assert.Equal(20, result.Seed.ProductsSeeded);
            Assert.Equal(20, result.Embedding?.ProductsEmbedded);
            Assert.Equal(SharedWorkerStatus.Ready, result.Status);

            // The worker emits camel-case payloads after AddDurableAI configures its owned client.
            // The SDK default converter cannot bind the nested Pascal-case records from that payload.
            // This assertion makes the cross-boundary configuration requirement observable.
            var incompatibleResult = await incompatibleCaller
                .GetWorkflowHandle<SharedWorkerStatusWorkflow>(handle.Id)
                .GetResultAsync<SharedWorkerResult>();
            Assert.Null(incompatibleResult.Migration);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

[Workflow("TemporalCommunity.Extensions.AI.IntegrationTests.SharedWorkerStatusWorkflow")]
public sealed class SharedWorkerStatusWorkflow
{
    [WorkflowRun]
    public Task<SharedWorkerResult> RunAsync() => Task.FromResult(new SharedWorkerResult(
        new SharedWorkerMigrationResult(1, "schema-created"),
        new SharedWorkerSeedResult(6, 20),
        new SharedWorkerEmbeddingResult(20),
        SharedWorkerStatus.Ready));
}

public sealed record SharedWorkerMigrationResult(int AppliedCount, string Message);

public sealed record SharedWorkerSeedResult(int CategoriesSeeded, int ProductsSeeded);

public sealed record SharedWorkerEmbeddingResult(int ProductsEmbedded);

public sealed record SharedWorkerResult(
    SharedWorkerMigrationResult Migration,
    SharedWorkerSeedResult Seed,
    SharedWorkerEmbeddingResult? Embedding,
    SharedWorkerStatus Status);

public enum SharedWorkerStatus
{
    Ready = 0,
}
