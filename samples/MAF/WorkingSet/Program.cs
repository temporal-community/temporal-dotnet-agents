// WorkingSet — demonstrates WorkingSetContextProvider (Feature D).
//
// Scenario: multi-turn code assistant. Four turns that progressively build a
// working set from mock file reads. WorkingSetContextProvider scans the
// accumulated chat history after each turn, extracts recently-referenced file
// paths, and injects a compact "## Working set" system note before every LLM
// call. By Turn 4 the agent can answer "what files are we working with?" from
// injected context alone — no tool call needed.
//
// Run:  dotnet run --project samples/MAF/WorkingSet/WorkingSet.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using WorkingSet;
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
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/WorkingSet");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the file system singleton ───────────────────────────────
builder.Services.AddSingleton<FakeFileSystem>();

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient — the durable-agent path composes its own pipeline
// internally. Calling .UseFunctionInvocation() here would short-circuit Temporal's
// per-tool activity dispatch.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the durable agent ───────────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("working-set-sample")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("CodeAssistant", agent =>
        {
            agent.Instructions =
                "You are a helpful code assistant. Read files when asked and answer questions about code. " +
                "When the user asks which files are in the working set, refer to the ## Working set " +
                "context note that is provided to you — do not call list_files for that.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromMinutes(10); // shortened for demo

            // WorkingSetContextProvider: scans the accumulated ChatMessage history on every LLM
            // step, extracts recently-referenced file paths, and injects a compact system note:
            //
            //   ## Working set
            //   Recently referenced files/paths in this session:
            //   - AuthService.cs
            //   - UserRepository.cs
            //
            // SilentMode defaults to false so the note is visible to the LLM.
            // State is persisted in AgentSessionStateBag under "temporal.working_set".
            agent.AddContextProvider(new WorkingSetContextProvider());

            // read_file: read-only tool, no retry override needed.
            agent.AddTool(
                "read_file",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<FakeFileSystem>().ReadFile,
                    name: "read_file",
                    description: "Read the contents of a source file by its filename."));

            // list_files: read-only, returns the available file names.
            agent.AddTool(
                "list_files",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<FakeFileSystem>().ListFiles,
                    name: "list_files",
                    description: "List all files available in this repository."));
        });
    });

// ── Step 6: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running four-turn code assistant session...\n");

var proxy = host.Services.GetTemporalAgentProxy("CodeAssistant");
var session = await proxy.CreateSessionAsync();

// ── Turn 1: Read AuthService.cs ───────────────────────────────────────────────
Console.WriteLine("User : Show me the AuthService implementation");
var r1 = await proxy.RunAsync("Show me the AuthService implementation", session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}");
Console.WriteLine();

// ── Turn 2: Read UserRepository.cs ───────────────────────────────────────────
// WorkingSetContextProvider now has AuthService.cs in the working set.
Console.WriteLine("User : What does UserRepository look like?");
var r2 = await proxy.RunAsync("What does UserRepository look like?", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}");
Console.WriteLine();

// ── Turn 3: Cross-file question ───────────────────────────────────────────────
// Working set: AuthService.cs + UserRepository.cs.
Console.WriteLine("User : How does auth relate to the repository?");
var r3 = await proxy.RunAsync("How does auth relate to the repository?", session);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}");
Console.WriteLine();

// ── Turn 4: Working-set query — no tool call needed ──────────────────────────
// The provider injects a "## Working set" note before this LLM call listing
// both AuthService.cs and UserRepository.cs. The agent can answer from context.
Console.WriteLine("User : What files are we working with in this session?");
var r4 = await proxy.RunAsync("What files are we working with in this session?", session);
Console.WriteLine($"Agent: {r4.Text ?? "(no response)"}");
Console.WriteLine();

// ── Step 7: Graceful shutdown ─────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }

Console.WriteLine("Done.");
