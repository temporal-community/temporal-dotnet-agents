// DirectAdapters — demonstrates a hand-written Activity (ResearchActivities) making a single
// LLM call durable, and AIFunction.AsDurable() making a single tool call durable, composed
// inside a fully custom [Workflow] with no session/history/HITL machinery.
//
// Run:  dotnet run --project samples/MEAI/DirectAdapters/DirectAdapters.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException(
        "OPENAI_API_BASE_URL is not configured. Set it in appsettings.json, " +
        "as an environment variable, or via " +
        "`dotnet user-secrets set OPENAI_API_BASE_URL https://api.openai.com/v1 --project samples/MEAI/DirectAdapters`.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it as an environment variable or via " +
        "`dotnet user-secrets set OPENAI_API_KEY sk-... --project samples/MEAI/DirectAdapters`. " +
        "Note: user secrets only load in the Development environment (DOTNET_ENVIRONMENT unset or set to 'Development').");

// ── Setup: Weather tool (registered in DurableFunctionRegistry) ───────────────
// Real implementation, resolved by name ("get_current_weather") when
// ResearchWorkflow dispatches a durable tool call via AsDurable().
static async Task<string> GetCurrentWeather(string city)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    return Random.Shared.NextDouble() > 0.5
        ? $"It's sunny and 22 °C in {city}."
        : $"It's overcast and 15 °C in {city}.";
}

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_current_weather",
    description: "Returns the current weather conditions for a given city.");

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// This is the real, worker-side client that ResearchActivities constructor-injects.
// Workflow code never touches it directly — ResearchWorkflow dispatches to
// ResearchActivities.SummarizeWeatherAsync via Workflow.ExecuteActivityAsync, and the Temporal
// worker resolves this registration when the activity attempt runs.
IChatClient openAiChatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model).AsIChatClient();

builder.Services.AddChatClient(openAiChatClient);

// ── Setup: Connect Temporal client with DurableAIDataConverter ───────────────
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register worker ────────────────────────────────────────────────────
// AddDurableAI wires DurableChatActivities and DurableFunctionActivities on this worker.
// RegisterDefaultWorkflow = false suppresses DurableChatWorkflow — this sample never creates
// a DurableChatSessionClient; ResearchWorkflow is a fully custom [Workflow] instead.
// AddDurableTools registers weatherTool in the DurableFunctionRegistry so AsDurable() calls
// inside the workflow can resolve it by name. AddSingletonActivities<ResearchActivities>
// registers the hand-written Activity that makes the LLM call durable (constructor-injects the
// openAiChatClient registered above via AddChatClient). AddWorkflow registers ResearchWorkflow
// itself.
builder.Services
    .AddHostedTemporalWorker(DirectAdaptersDemo.TaskQueue)
    .AddDurableAI(opts =>
    {
        opts.RegisterDefaultWorkflow = false;
        opts.ActivityTimeout = TimeSpan.FromMinutes(2);
    })
    .AddDurableTools(weatherTool)
    .AddSingletonActivities<ResearchActivities>()
    .AddWorkflow<ResearchWorkflow>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demo ──────────────────────────────────────────────────────────────────
await DirectAdaptersDemo.RunAsync(temporalClient, "Seattle");

// ── Stop ──────────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
