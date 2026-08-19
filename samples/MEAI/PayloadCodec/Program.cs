using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.AI;

const string TaskQueue = "durable-ai-payload-codec";
var temporalAddress = Environment.GetEnvironmentVariable("TEMPORAL_ADDRESS") ?? "localhost:7233";

// Keep the codec and converter construction in one shared application method. In split
// deployments, call the same method from the client, workflow worker, activity worker, replayer,
// and any operational reader that must inspect these payloads.
static Temporalio.Converters.DataConverter CreateDataConverter() =>
    DurableAIDataConverter.CreateDataConverter(new DurableAIGzipPayloadCodec(
        new DurableAIGzipPayloadCodecOptions
        {
            MinimumPayloadSizeBytes = 1024,
            MaximumEncodedPayloadSizeBytes = 2 * 1024 * 1024,
            MaximumDecodedPayloadSizeBytes = 4 * 1024 * 1024,
            MinimumCompressionSavingsRatio = 0.05,
        }));

var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    Namespace = "default",
    DataConverter = CreateDataConverter(),
});

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ITemporalClient>(client);
builder.Services
    .AddHostedTemporalWorker(TaskQueue)
    .AddWorkflow<CodecEchoWorkflow>();

using var host = builder.Build();
await host.StartAsync();

var input = string.Concat(Enumerable.Repeat("compressible durable AI payload;", 1000));
var result = await client.ExecuteWorkflowAsync(
    (CodecEchoWorkflow workflow) => workflow.RunAsync(input),
    new WorkflowOptions($"payload-codec-{Guid.NewGuid():N}", TaskQueue));

Console.WriteLine($"Round-trip succeeded: {result.Length:N0} characters.");
await host.StopAsync();

[Workflow]
public sealed class CodecEchoWorkflow
{
    [WorkflowRun]
    public Task<string> RunAsync(string input) => Task.FromResult(input);
}
