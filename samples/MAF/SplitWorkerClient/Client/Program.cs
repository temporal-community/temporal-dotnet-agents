// SplitWorkerClient / Client — sends messages to agent sessions running in the Worker.
// Has no IChatClient or worker registrations; requires only a Temporal connection.
//
// Run:  dotnet run --project samples/MAF/SplitWorkerClient/Client/Client.csproj

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

// ── Step 1: Register client-side Temporal infrastructure only ────────────────
// AddTemporalAgentProxies wires up the ITemporalAgentClient and keyed AIAgent proxies.
// The Temporal client connection is registered separately.
//
// Crucially, NO worker, NO AgentWorkflow, NO AgentActivities, and NO IChatClient
// are registered here. All of that lives in the Worker process.
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services.AddTemporalAgentProxies("agents", "Assistant");

var host = builder.Build();
await host.StartAsync();

// ── Step 2: Resolve the proxy ─────────────────────────────────────────────────
// The proxy is a TemporalAIAgentProxy: calling RunAsync on it sends a
// WorkflowUpdate to the AgentWorkflow running in the Worker process.
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
// ── Step 3: Create a session ──────────────────────────────────────────────────
// CreateSessionAsync generates a TemporalAgentSessionId (workflow ID: ta-assistant-{guid}).
// The AgentWorkflow is started in the Worker when the first message is sent.
var session = await proxy.CreateSessionAsync();
Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 4: Multi-turn conversation ───────────────────────────────────────────
// Each RunAsync is a durable WorkflowUpdate — the Worker processes it,
// runs the agent activity, appends to conversation history, and returns the response.
// Context is preserved across turns because history lives in the workflow state.
var r1 = await proxy.RunAsync("What is the capital of France?", session);
Console.WriteLine($"User : What is the capital of France?");
Console.WriteLine($"Agent: {r1.Text}\n");

var r2 = await proxy.RunAsync("What is its population?", session);
Console.WriteLine($"User : What is its population?");
Console.WriteLine($"Agent: {r2.Text}\n");

var r3 = await proxy.RunAsync("What's the current weather condition?", session);
Console.WriteLine($"User : What's the current weather condition?");
Console.WriteLine($"Agent: {r3.Text}\n");

// ── Step 5: Reuse the session from a new client instance ─────────────────────
// Sessions are durable: session.ToString() returns the workflow ID (e.g. "ta-assistant-abc123"),
// never null at runtime — the ! suppresses the compiler's string? inference on object.ToString().
// Parsing it back simulates a second process that only has the workflow ID (e.g. stored in a
// database) reconstructing the session handle to continue an existing conversation.
var sessionId = TemporalAgentSessionId.Parse(session.ToString()!);
var resumedSession = new TemporalAgentSession(sessionId);

var r4 = await proxy.RunAsync("Summarize what we discussed.", resumedSession);
Console.WriteLine($"User : Summarize what we discussed.");
Console.WriteLine($"Agent: {r4.Text}\n");

// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
