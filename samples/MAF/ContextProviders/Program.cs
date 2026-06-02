// ContextProviders — individual MAF context providers with Temporalio.Extensions.Agents (v0.3).
//
// Demonstrates registering MAF's TodoProvider and AgentModeProvider via
// DurableAgentBuilder.AddContextProvider. The individual providers are standard
// AIContextProvider subclasses and work today — only the full HarnessAgent bundle
// is incompatible (see docs/how-to/MAF/harness-agent-compatibility.md).
//
// Providers fire once per LLM step, not once per turn, so keep them idempotent.
// State is stored in AgentSessionStateBag and survives worker restarts and
// continue-as-new transitions automatically.
//
// Run:  dotnet run --project samples/MAF/ContextProviders/ContextProviders.csproj

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Extensions.Agents;

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

// ── Step 3: Create the context providers ─────────────────────────────────────
// TodoProvider: gives the agent todos_add / todos_complete / todos_get_remaining
// and injects the current todo list as a context message on every LLM step.
// AgentModeProvider: tracks "plan" / "execute" mode in session state and injects
// mode-specific instructions. Both persist in AgentSessionStateBag — no extra
// storage is needed.
// MAAI001: TodoProvider and AgentModeProvider are marked [Experimental] in MAF.
// Suppress the diagnostic for the entire file — all usages are intentional.
#pragma warning disable MAAI001
var todoProvider = new TodoProvider();
var modeProvider = new AgentModeProvider();

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the durable agent with both context providers ────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("PlannerAgent", agent =>
        {
            agent.Instructions =
                "You are a helpful planning assistant. " +
                "When the user describes a task, break it down into todos. " +
                "Start in plan mode and switch to execute mode when the user says 'go'.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1); // shortened for demo

            // TodoProvider supplies todo management tools and injects the current
            // todo list as a context message before each LLM call.
            agent.AddContextProvider(todoProvider);

            // AgentModeProvider supplies mode_get / mode_set tools and injects
            // mode-specific instructions before each LLM call.
            agent.AddContextProvider(modeProvider);
        });
    });

// ── Step 6: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Sending messages...\n");

// ── Step 7: Resolve the proxy and open a session ─────────────────────────────
var proxy = host.Services.GetTemporalAgentProxy("PlannerAgent");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 8: Multi-turn conversation exercising both providers ─────────────────
// Turn 1: ask the agent to plan a task — it should add todos in plan mode.
Console.WriteLine("User : I need to write a blog post about Temporal durable agents.");
var r1 = await proxy.RunAsync(
    "I need to write a blog post about Temporal durable agents.", session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

// Turn 2: tell the agent to proceed — it should switch to execute mode.
Console.WriteLine("User : Go ahead and execute the plan.");
var r2 = await proxy.RunAsync("Go ahead and execute the plan.", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

// Turn 3: ask what remains — TodoProvider's state survives across turns.
Console.WriteLine("User : What todos are still open?");
var r3 = await proxy.RunAsync("What todos are still open?", session);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}\n");

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");
