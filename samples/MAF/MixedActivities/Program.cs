// MixedActivities — demonstrates mixing regular [Activity] methods with durable agent calls
// in the same Temporal workflow.
//
// The pipeline processes three support documents in parallel:
//   1. FetchDocumentAsync  (regular activity — data I/O)
//   2. agent.RunAsync      (durable agent — AI text analysis)
//   3. StoreAnalysisAsync  (regular activity — persist result)
//   4. NotifyReviewerAsync (regular activity — reviewer notification)
//
// Run:  dotnet run --project samples/MAF/MixedActivities/MixedActivities.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MixedActivities;
using OpenAI;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/MixedActivities");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
const string taskQueue = "mixed-activities";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
// The durable-agent path composes its pipeline internally — do NOT call
// .UseFunctionInvocation() here. The DocumentAnalyst agent has no tools, so
// there is no pipeline composition difference in this sample, but the rule
// applies universally.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register Temporal client ─────────────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");

// ── Key pattern: mixing regular activities with durable agent calls ────────────
// The worker registers both DocumentActivities (plain [Activity] methods) and the
// DocumentAnalyst durable agent on the same task queue. DocumentPipelineWorkflow
// orchestrates them in sequence — regular activities for data I/O, the durable
// agent for AI reasoning. Both are fully durable: a worker crash at any step
// replays from history without re-executing completed steps.

// ── Step 5: Register activities, agent, and workflow on the same worker ───────
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddSingletonActivities<DocumentActivities>()   // regular [Activity] methods
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("DocumentAnalyst", agent =>
        {
            agent.Instructions =
                "You analyze support documents. Reply with exactly two lines: " +
                "'Category: <Bug|Feature|Billing|Other>' and 'Summary: <one sentence>'. No preamble.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromMinutes(10);
            // No tools needed — pure text analysis via the LLM.
        });
    })
    .AddWorkflow<DocumentPipelineWorkflow>();        // orchestrating workflow

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting document analysis workflows...\n");

// ── Step 7: Submit all three documents as parallel workflows ──────────────────
var client = host.Services.GetRequiredService<ITemporalClient>();

var docIds = new[] { "doc-001", "doc-002", "doc-003" };

var handles = await Task.WhenAll(docIds.Select(docId =>
    client.StartWorkflowAsync(
        (DocumentPipelineWorkflow wf) => wf.RunAsync(docId),
        new WorkflowOptions
        {
            Id = $"doc-pipeline-{docId}-{Guid.NewGuid():N}",
            TaskQueue = taskQueue,
        })));

Console.WriteLine($"Submitted {handles.Length} workflows. Waiting for results...\n");

// ── Step 8: Collect and print results ────────────────────────────────────────
var results = await Task.WhenAll(handles.Zip(docIds, async (handle, docId) =>
{
    try
    {
        var analysisText = await handle.GetResultAsync();
        return (docId, result: analysisText, error: (string?)null);
    }
    catch (Exception ex)
    {
        return (docId, result: (string?)null, error: ex.Message);
    }
}));

Console.WriteLine("─── Analysis Results ────────────────────────────────────────");
foreach (var (docId, result, error) in results)
{
    Console.WriteLine($"\n  Document: {docId}");
    if (error is not null)
    {
        Console.WriteLine($"  Error:    {error}");
    }
    else
    {
        // Print each line of the agent's response indented.
        foreach (var line in (result ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Console.WriteLine($"  {line}");
    }
}

Console.WriteLine("\n─── Temporal Web UI ─────────────────────────────────────────");
Console.WriteLine("  Open http://localhost:8233 to inspect the workflow event histories.");
Console.WriteLine("  Each workflow shows distinct activity rows:");
Console.WriteLine("    • FetchDocumentAsync   — plain [Activity], no AI");
Console.WriteLine("    • RunDurableAgentStep  — LLM call dispatched by the durable agent");
Console.WriteLine("    • StoreAnalysisAsync   — plain [Activity], no AI");
Console.WriteLine("    • NotifyReviewerAsync  — plain [Activity], no AI");

// ── Step 9: Graceful shutdown ─────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("\nDone.");
