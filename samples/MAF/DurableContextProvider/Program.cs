// DurableContextProvider — demonstrates IDurableToolSource and DurableToolRegistrationSpec.
//
// Context providers can contribute durable tools in two ways:
//
//   Approach A (IDurableToolSource, this file):
//     Implement IDurableToolSource on your AIContextProvider subclass. The framework calls
//     GetDurableTools() once at registration time and registers the tools as Temporal activities.
//
//   Approach B (explicit DurableToolRegistrationSpec, see second agent below):
//     Pass specs directly to AddContextProvider(provider, durableTools). Use this when you
//     don't control the provider type (e.g. a third-party AIContextProvider).
//
// Run:  dotnet run --project samples/MAF/DurableContextProvider/DurableContextProvider.csproj
//
// Prerequisites: temporal server start-dev + OPENAI_API_KEY (via dotnet user-secrets or env).

#pragma warning disable TA001 // IDurableToolSource is experimental; intentional usage in sample

using System.ClientModel;
using DurableContextProvider;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Tools;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("TemporalCommunity.Extensions.Agents", LogLevel.Information);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/DurableContextProvider");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register Temporal client + worker ─────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("durable-context-provider")
    .AddTemporalAgents(opts =>
    {
        // ── Approach A: IDurableToolSource ─────────────────────────────────────
        // SearchContextProvider implements IDurableToolSource, so the framework extracts
        // GetDurableTools() at registration and registers web_search as a durable activity.
        // This is the preferred path when you own the provider type.
        opts.AddDurableAgent("SearchAgent", agent =>
        {
            agent.Instructions =
                "You are a research assistant. When asked about current events or facts, " +
                "use the web_search tool to find relevant information. " +
                "Summarize findings concisely.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 5;

            // SearchContextProvider implements IDurableToolSource — the framework registers
            // web_search as a durable Temporal activity automatically.
            // In the Temporal Web UI you'll see:
            //   • RunDurableAgentStep              — one row per LLM call
            //   • InvokeAgentTool:SearchAgent:web_search — each search is its own activity
            agent.AddContextProvider(new SearchContextProvider());
        });

        // ── Approach B: explicit DurableToolRegistrationSpec ──────────────────
        // Use this when you don't control the provider type. The provider is transparently
        // wrapped in DurableContextProviderWrapper, which implements IDurableToolSource.
        // Non-idempotent write tools MUST call opts.NoRetry() to prevent double-execution.
        opts.AddDurableAgent("WeatherAgent", agent =>
        {
            agent.Instructions =
                "You are a helpful weather assistant. Use get_weather to look up current " +
                "conditions for any location the user asks about.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 3;

            // StatelessContextProvider is a simple provider that injects a date/time note.
            // It doesn't own any tools, so we pass the tool specs as an explicit second argument.
            // The framework wraps the provider in DurableContextProviderWrapper and registers
            // get_weather as a durable Temporal activity.
            var weatherTool = AIFunctionFactory.Create(
                (string location) =>
                    $"Weather in {location}: 72°F, partly cloudy. Wind: 8 mph SW. [stub]",
                name: "get_weather",
                description: "Get the current weather conditions for a location.");

            agent.AddContextProvider(
                new DateTimeContextProvider(),
                durableTools:
                [
                    // get_weather is read-only — safe to retry (default policy).
                    new DurableToolRegistrationSpec(weatherTool),
                ]);
        });
    });

// ── Step 5: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Step 6: Run both agents ───────────────────────────────────────────────────
var searchProxy = host.Services.GetTemporalAgentProxy("SearchAgent");
var weatherProxy = host.Services.GetTemporalAgentProxy("WeatherAgent");

// Approach A demo: SearchAgent uses IDurableToolSource
Console.WriteLine("=== Approach A: IDurableToolSource ===");
var searchSession = await searchProxy.CreateSessionAsync();
Console.WriteLine($"Session: {searchSession}\n");

Console.WriteLine("User : What are the latest developments in Temporal workflow orchestration?");
var r1 = await searchProxy.RunAsync(
    "What are the latest developments in Temporal workflow orchestration?", searchSession);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

// Approach B demo: WeatherAgent uses explicit DurableToolRegistrationSpec
Console.WriteLine("=== Approach B: DurableToolRegistrationSpec ===");
var weatherSession = await weatherProxy.CreateSessionAsync();
Console.WriteLine($"Session: {weatherSession}\n");

Console.WriteLine("User : What's the weather like in Seattle today?");
var r2 = await weatherProxy.RunAsync("What's the weather like in Seattle today?", weatherSession);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

Console.WriteLine("User : How about in Miami?");
var r3 = await weatherProxy.RunAsync("How about in Miami?", weatherSession);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}\n");

// ── Step 7: Operator guidance ─────────────────────────────────────────────────
Console.WriteLine("─── View the activity timeline ──────────────────────────────");
Console.WriteLine("  Open http://localhost:8233 in the Temporal Web UI.");
Console.WriteLine("  Each search/weather call appears as a distinct Temporal activity:");
Console.WriteLine("    • RunDurableAgentStep                          — one row per LLM call");
Console.WriteLine("    • InvokeAgentTool:SearchAgent:web_search       — durable, retriable");
Console.WriteLine("    • InvokeAgentTool:WeatherAgent:get_weather     — durable, retriable");
Console.WriteLine();

// ── Step 8: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
