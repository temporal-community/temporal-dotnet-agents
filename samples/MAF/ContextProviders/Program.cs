// ContextProviders — custom AIContextProvider subclasses with TemporalCommunity.Extensions.Agents (v0.3).
//
// Demonstrates registering custom AIContextProvider subclasses via
// DurableAgentBuilder.AddContextProvider. Providers fire once per LLM step, not once
// per turn, so keep them idempotent. Stateful providers persist their data in
// AgentSessionStateBag so it survives worker restarts and continue-as-new transitions.
//
// Two providers are registered:
//   TurnCounterProvider — increments a session-scoped LLM-call counter in StateBag
//                         and injects it as a system message before each LLM call.
//   DateTimeProvider    — injects the current UTC date/time on every step (stateless).
//
// Run:  dotnet run --project samples/MAF/ContextProviders/ContextProviders.csproj

using System.ClientModel;
using ContextProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ContextProviders");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register the durable agent with both context providers ────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions =
                "You are a helpful assistant. Track how many LLM calls have happened " +
                "in this session and report that information when asked.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 3; // no tools needed — providers supply all context
            agent.TimeToLive = TimeSpan.FromHours(1); // shortened for demo

            // TurnCounterProvider: increments a session counter in StateBag and injects it
            // as a system message before every LLM call.
            agent.AddContextProvider(new TurnCounterProvider());

            // DateTimeProvider: stateless — injects the current UTC time on every LLM call.
            agent.AddContextProvider(new DateTimeProvider());
        });
    });

// ── Step 5: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Sending messages...\n");

// ── Step 6: Resolve the proxy and open a session ─────────────────────────────
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 7: Multi-turn conversation exercising both providers ─────────────────
// Turn 1: the counter starts at 1; both providers inject their context messages.
Console.WriteLine("User : Hello! How many times have you been called so far in this session?");
var r1 = await proxy.RunAsync(
    "Hello! How many times have you been called so far in this session?", session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

// Turn 2: the agent uses the DateTimeProvider context to answer.
Console.WriteLine("User : What's the current time?");
var r2 = await proxy.RunAsync("What's the current time?", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

// Turn 3: the counter has incremented across all prior LLM steps.
Console.WriteLine("User : How many total LLM calls have we had now?");
var r3 = await proxy.RunAsync("How many total LLM calls have we had now?", session);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}\n");

// ── Step 8: Graceful shutdown ────────────────────────────────────────────────
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");
