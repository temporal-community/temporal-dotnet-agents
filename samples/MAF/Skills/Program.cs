// Skills — progressive-disclosure skills with TemporalCommunity.Extensions.Agents (v0.3).
//
// Demonstrates UseSkills(Action<SkillsBuilder>) to wire up a compact skill index
// and on-demand load_skill dispatch as separate InvokeAgentTool Temporal activities.
//
// Two skill types are registered:
//   File-based  — skill-catalog/expense-report/SKILL.md, scanned via AddSkillsFromDirectory.
//   Inline      — meeting-summary, registered directly in code via AddSkill.
//
// Two conversation turns show the agent loading each skill on demand:
//   Turn 1: ask about expense reporting → agent calls load_skill("expense-report")
//   Turn 2: ask about meeting summaries → agent calls load_skill("meeting-summary")
//
// Run:  dotnet run --project samples/MAF/Skills/Skills.csproj

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Skills;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/Skills");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient — UseSkills composes its own tool-invocation loop
// internally. Calling .UseFunctionInvocation() here would short-circuit Temporal's
// per-tool activity dispatch and silently skip observability and durability.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Define the inline meeting-summary skill ──────────────────────────
// AgentInlineSkill: name, description (shown in the injected skill index), content
// (returned verbatim by load_skill).
var meetingSummarySkill = new AgentInlineSkill(
    "meeting-summary",
    "Guide for writing concise meeting summaries.",
    "## Meeting Summary\n\nCapture: attendees, decisions, action items with owners and due dates. Keep under 250 words.");

// ── Step 5: Register the durable agent with skills ───────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("skills-maf")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("TaskAssistant", agent =>
        {
            agent.Instructions =
                "You are a helpful task assistant. When the user asks about a process or " +
                "topic that matches one of your available skills, use the load_skill tool " +
                "to retrieve the full instructions and share them with the user.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1); // shortened for demo

            // Wire up both skill sources. The SkillsContextProvider injects a compact
            // index (name + description per skill) as a system message before every LLM
            // call so the agent knows which skills are available without loading them all
            // upfront. load_skill is dispatched as a separate InvokeAgentTool Temporal
            // activity, giving per-skill observability and retry in the Web UI.
            agent.UseSkills(s =>
            {
                // File-based: scans skill-catalog/ for SKILL.md files up to 2 levels deep.
                // Anchored to AppContext.BaseDirectory so the path resolves correctly
                // regardless of the caller's working directory (dotnet run from repo root,
                // IDE debugger, or the sample-canary recipe all work the same way).
                // Note: the directory is named "skill-catalog" (not "skills") to avoid a
                // case-insensitive filesystem collision with the compiled "Skills" executable
                // on macOS.
                s.AddSkillsFromDirectory(
                    Path.Combine(AppContext.BaseDirectory, "skill-catalog"));

                // Inline: registered directly — no file I/O, content is a C# string.
                s.AddSkill(meetingSummarySkill);
            });
        });
    });

// ── Step 6: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running two-turn task assistant session...\n");

// ── Step 7: Resolve the proxy and open a session ─────────────────────────────
var proxy = host.Services.GetTemporalAgentProxy("TaskAssistant");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 8: Multi-turn conversation exercising both skills ────────────────────

// Turn 1: expense-report skill — agent calls load_skill("expense-report") to pull
// the full SKILL.md content and shares the filing instructions.
const string turn1 = "How do I file an expense report? Please walk me through the process.";
Console.WriteLine($"User : {turn1}");
var r1 = await proxy.RunAsync(turn1, session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

// Turn 2: meeting-summary skill — agent calls load_skill("meeting-summary") to
// retrieve the inline skill content and explains the format.
const string turn2 = "Can you help me write a good meeting summary? What should I include?";
Console.WriteLine($"User : {turn2}");
var r2 = await proxy.RunAsync(turn2, session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

// ── Step 9: Signal Shutdown to the agent workflow ─────────────────────────────
// ShutdownAsync tells the workflow's run loop to stop accepting new turns and
// complete cleanly. Without it the workflow remains open for the full TTL.
if (session is TemporalAgentSession temporalSession)
{
    var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();
    await agentClient.ShutdownAsync(temporalSession.SessionId);
    Console.WriteLine("Shutdown signal sent to agent workflow.\n");
}

// ── Step 10: Graceful shutdown ────────────────────────────────────────────────
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");
