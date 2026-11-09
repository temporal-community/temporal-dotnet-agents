// ContextProviders — custom AIContextProvider subclasses with TemporalCommunity.Extensions.Agents.
//
// Demonstrates registering custom AIContextProvider subclasses via
// DurableAgentBuilder.AddContextProvider. In managed sessions, providers prepare
// context once per logical turn and the workflow reuses it across model steps.
// Stateful providers persist their data in AgentSessionStateBag.
//
// Two providers are registered:
//   TurnCounterProvider — increments a session-scoped turn counter in StateBag
//                         and injects it as a system message before each turn.
//   DateTimeProvider    — injects the current UTC date/time once per turn (stateless).
// A context-only MessageCountingChatReducer also limits eligible older turns for each model call.
//
// Run:  dotnet run --project samples/MAF/ContextProviders/ContextProviders.csproj

using System.ClientModel;
using ContextProviders;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.AI.Exceptions;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
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

#pragma warning disable MEAI001
builder.Services.AddKeyedSingleton<IChatReducer>(
    "recent-context",
    new MessageCountingChatReducer(20));
#pragma warning restore MEAI001

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
        opts.DefaultContextReducerKey = "recent-context";
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions =
                "You are a helpful assistant. Track how many turns have happened " +
                "in this session and report that information when asked.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 3; // no tools needed — providers supply all context
            agent.MaxModelContextTokens = 8192;
            agent.ModelOutputTokenReserve = 1024;
            agent.TimeToLive = TimeSpan.FromHours(1); // shortened for demo

            // TurnCounterProvider: increments a session counter in StateBag and injects it
            // before the model/tool loop for each turn.
            agent.AddContextProvider(new TurnCounterProvider());

            // DateTimeProvider: stateless — injects the current UTC time once per turn.
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
Console.WriteLine("User : How many turns have we had so far in this session?");
var r1 = await RunTurnAsync("How many turns have we had so far in this session?");
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

// Turn 2: the agent uses the DateTimeProvider context to answer.
Console.WriteLine("User : What's the current time?");
var r2 = await RunTurnAsync("What's the current time?");
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

// Turn 3: the counter has incremented once for each prior logical turn.
Console.WriteLine("User : How many total turns have we had now?");
var r3 = await RunTurnAsync("How many total turns have we had now?");
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

async Task<AgentResponse> RunTurnAsync(string message)
{
    var requestId = Guid.NewGuid().ToString("N");
    try
    {
        return await proxy.RunAsync(message, session, new TemporalAgentRunOptions { RequestId = requestId });
    }
    catch (DurableTurnFailedException failure)
    {
        Console.Error.WriteLine(
            $"Request {requestId} failed at committed revision {failure.CommittedRevision}. " +
            $"Calls: {failure.ToolCallCounts}. Reconcile external effects before a new request.");
        throw;
    }
    catch (DurableTurnOutcomeTooLargeException outcome)
    {
        Console.Error.WriteLine(
            $"Request {requestId} has a retained {(outcome.IsMinimal ? "minimal record" : "compact record")}. " +
            $"Calls: {outcome.ToolCallCounts}. The turn was not redispatched.");
        throw;
    }
}
