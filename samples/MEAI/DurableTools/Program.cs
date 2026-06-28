// DurableTools — demonstrates AsDurable(), which dispatches each tool call as its own
// Temporal activity (DurableFunctionActivities) rather than running the function inline.
//
// Run:  dotnet run --project samples/MEAI/DurableTools/DurableTools.csproj

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

const string taskQueue = "durable-tools";

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which handles MEAI's $type discriminator for
// polymorphic AIContent subclasses (TextContent, FunctionCallContent, etc.).
// Without this, type information is lost when types round-trip through history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Weather tool (registered in DurableFunctionRegistry) ───────────────
// GetCurrentWeather is the real implementation — registered via AddDurableTools()
// so DurableFunctionActivities can resolve it by name ("get_current_weather")
// when WeatherReportWorkflow dispatches a durable tool call.
static async Task<string> GetCurrentWeather(string city)
{
    // Simulate a short network round-trip. Without this delay the activity
    // completes so quickly that the Temporal dev-server UI fires its
    // legacy __workflow_definitions query at the exact same moment the
    // workflow tries to complete, causing an SDK activation conflict.
    await Task.Delay(TimeSpan.FromSeconds(1));
    return Random.Shared.NextDouble() > 0.5
        ? $"It's sunny and 22 °C in {city}."
        : $"It's overcast and 15 °C in {city}.";
}

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_current_weather",
    description: "Returns the current weather conditions for a given city.");

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// AddDurableAI registers DurableFunctionActivities (and supporting infrastructure)
// on the worker. No IChatClient is needed — this sample only exercises the
// per-tool durable activity path (Pattern 2), not the chat session path.
//
// RegisterDefaultWorkflow = false suppresses DurableChatWorkflow registration.
// This sample never creates a DurableChatSessionClient, so registering that
// workflow would be unused baggage on the worker.
//
// AddDurableTools registers weatherTool in the DurableFunctionRegistry so
// DurableFunctionActivities can resolve it by name at activity execution time.
// AddWorkflow<WeatherReportWorkflow> registers the custom workflow with the worker.
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.RegisterDefaultWorkflow = false;   // no DurableChatSessionClient used here
    })
    .AddDurableTools(weatherTool)
    .AddWorkflow<WeatherReportWorkflow>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demos ─────────────────────────────────────────────────────────────────
// Run the demo for two cities sequentially to show that each workflow
// execution dispatches a separate, independently tracked activity.
await DurableToolDemo.RunAsync(temporalClient, taskQueue, "Tokyo");
await DurableToolDemo.RunAsync(temporalClient, taskQueue, "London");

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
