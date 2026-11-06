// BasicAgent — single durable agent session via TemporalCommunity.Extensions.Agents.
//
// Demonstrates MAF ChatClientAgentOptions registration, Temporal-only settings in
// TemporalAgentOptions, keyed AIAgent proxy resolution, and a scoped service resolved
// from AIFunctionArguments.Services for each durable tool invocation.
//
// Run:  dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/BasicAgent");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient and scoped tool dependency in DI ─────────
// The durable-agent path owns tool dispatch. Do not call .UseFunctionInvocation()
// on the chat client.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddScoped<WeatherService>();

static string GetCurrentWeather(
    string city,
    IServiceProvider services) =>
    services.GetRequiredService<WeatherService>().GetCurrentWeather(city);

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_weather",
    description: "Returns the current weather conditions for a city.");

// ── Step 4: Register the durable agent ───────────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(agents =>
        agents.AddTemporalAgent(
            "Assistant",
            new ChatClientAgentOptions
        {
                Description = "A geography and weather assistant.",
                ChatOptions = new ChatOptions
                {
                    Instructions =
                        "You are a helpful geography and weather assistant. " +
                        "When asked about weather, always use the get_weather tool.",
                    Tools = [weatherTool],
                },
            },
            new TemporalAgentOptions
            {
                TimeToLive = TimeSpan.FromHours(1), // shortened for demo; production default is 14 days
            }));

// ── Step 5: Start the host (worker runs as IHostedService) ───────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Sending messages...\n");

// ── Step 6: Resolve the proxy and open a session ─────────────────────────────
// Resolve the keyed TemporalAIAgentProxy through the MAF AIAgent surface.
var proxy = host.Services.GetRequiredKeyedService<AIAgent>("Assistant");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 7: Multi-turn conversation ──────────────────────────────────────────
// Each RunAsync call is a Temporal WorkflowUpdate — a durable, acknowledged
// request/response round-trip. Conversation history is preserved in the workflow.
Console.WriteLine("User : What is the capital of France?");
var r1 = await proxy.RunAsync(
    "What is the capital of France?",
    session,
    new TemporalAgentRunOptions { RequestId = "basic-agent-turn-1" });
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

Console.WriteLine("User : What is its population?");
var r2 = await proxy.RunAsync("What is its population?", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

Console.WriteLine("User : What is the weather like there right now?");
var r3 = await proxy.RunAsync("What is the weather like there right now?", session);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}\n");

// ── Step 8: Request shutdown from the agent workflow ─────────────────────────
// Request shutdown so the workflow exits its session loop rather than sitting parked for TimeToLive.
if (session is TemporalAgentSession temporalSession)
{
    var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();
    await agentClient.ShutdownAsync(temporalSession.SessionId);
    Console.WriteLine("Shutdown update accepted by agent workflow.\n");
}

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
// The Temporal hosted worker cancels its poll loop on shutdown, which may surface as
// OperationCanceledException through BackgroundService. Swallow it: it is expected.
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");

sealed class WeatherService
{
    public string GetCurrentWeather(string city) =>
        $"{city}: " + (Random.Shared.NextDouble() > 0.5 ? "It's sunny" : "It's raining");
}
