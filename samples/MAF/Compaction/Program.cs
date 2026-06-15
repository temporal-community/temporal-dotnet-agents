// Compaction — durable agent session demonstrating in-session history compaction.
//
//   • UseCompaction("summarization") triggers a CompactHistory activity after every
//     final-step turn whose total history exceeds the strategy's threshold.
//   • InMemoryCompactionAwareStore implements the full marker-aware projection contract
//     (applyCompaction: false → audit canonical; true → projected post-compact view).
//   • The driver runs 8 turns, then prints the audit canonical view (every entry the
//     store holds) alongside the projected view the LLM has been seeing.
//   • Finally, demonstrates a GDPR erasure cascade via CompactionAwareErasureHelper.
//
// Run:  temporal server start-dev   (one terminal)
//       dotnet run --project samples/MAF/Compaction/Compaction.csproj

using System.ClientModel;
using Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Session;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Agents.Compaction;
using Temporalio.Extensions.Agents.HistoryStore;

// ── Step 1: Build the host ───────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/Compaction");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Wire DI ──────────────────────────────────────────────────────────
// The store is a singleton so the demo driver can inspect raw + projected views
// after the conversation completes.
builder.Services.AddSingleton<InMemoryCompactionAwareStore>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient(temporalAddress, "default");

// Override the built-in "summarization" strategy with low thresholds so the demo
// triggers within a handful of turns. AddTemporalAgents pre-registers the default
// summarization (trigger=30, keep=10) via TryAddKeyedSingleton; this AddKeyedSingleton
// MUST be done BEFORE AddTemporalAgents so it wins (TryAdd skips when a key is already
// present).
builder.Services.AddKeyedSingleton<ICompactionStrategy>(
    SummarizationCompactionStrategy.Key,
    new SummarizationCompactionStrategy(
        triggerEntryCount: 6,   // each turn = 2 entries (request + response), so trigger after ~3 turns
        keepRecentCount: 2,
        systemPrompt: SummarizationCompactionStrategy.DefaultSystemPrompt));

// ── Step 4: Register the agent ───────────────────────────────────────────────
const string taskQueue = "compaction-demo";
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        // Compaction REQUIRES an external history store — markers live there.
        opts.HistoryStore = sp => sp.GetRequiredService<InMemoryCompactionAwareStore>();

        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.Description = "Helpful customer support agent — long conversation triggers compaction.";
            agent.Instructions =
                "You are a friendly customer support agent for an online bookstore. " +
                "Answer concisely (1–2 sentences). Mention book titles, author names, " +
                "and order IDs verbatim when relevant. If asked about earlier in the conversation, " +
                "use the rollup summary you see in your system context.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);

            // Opt in to compaction. Built-in keys: "truncation", "sliding-window", "summarization".
            agent.CompactionStrategyKey = SummarizationCompactionStrategy.Key;
        });
    });

// ── Step 5: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Demonstrating in-session compaction.\n");

var store = host.Services.GetRequiredService<InMemoryCompactionAwareStore>();
var proxy = host.Services.GetTemporalAgentProxy("SupportAgent");
var session = (Temporalio.Extensions.Agents.Session.TemporalAgentSession)await proxy.CreateSessionAsync();
var sessionId = session.SessionId.WorkflowId;

Console.WriteLine($"Session: {sessionId}");
Console.WriteLine($"Compaction strategy: \"summarization\" (trigger=6, keep=2 — fires after ~3 turns)\n");

// ── Step 6: Drive 8 turns ────────────────────────────────────────────────────
string[] questions =
[
    "I'm looking for a sci-fi novel — recommend one.",
    "How much does it usually cost?",
    "What's the order ID format you use?",
    "Can I get free shipping?",
    "What's your return policy?",
    "Do you have audiobooks?",
    "What was the first book you recommended?",   // tests post-compact memory
    "And how much did you say it costs?",          // tests post-compact memory
];

for (int i = 0; i < questions.Length; i++)
{
    Console.WriteLine($"Turn {i + 1}: \"{questions[i]}\"");
    var response = await proxy.RunAsync(questions[i], session);
    var text = response.Text;
    Console.WriteLine($"Agent : {(string.IsNullOrEmpty(text) ? "(no response)" : text)}");

    // Snapshot the store state after each turn to show compaction firing.
    var raw = store.SnapshotRaw(sessionId);
    var markerCount = raw.Count(e => e is CompactionMarkerEntry);
    var nonMarkerCount = raw.Count - markerCount;
    Console.WriteLine(
        $"        Store: {raw.Count} total ({nonMarkerCount} source + {markerCount} marker)");
    Console.WriteLine();
}

// ── Step 7: Print audit canonical vs projected views side-by-side ────────────
Console.WriteLine("════════════════════════════════════════════════════════════════════");
Console.WriteLine(" View comparison");
Console.WriteLine("════════════════════════════════════════════════════════════════════\n");

var auditView = store.SnapshotRaw(sessionId);
var projectedView = store.SnapshotProjected(sessionId);

Console.WriteLine($"AUDIT CANONICAL ({auditView.Count} entries — applyCompaction: false):");
foreach (var entry in auditView)
{
    var kind = entry switch
    {
        CompactionMarkerEntry m => $"MARKER ({m.Strategy}, refs {m.CompactedMessageIds.Count})",
        _ => entry.GetType().Name,
    };
    Console.WriteLine($"  • {entry.CorrelationId[..Math.Min(20, entry.CorrelationId.Length)]}  {kind}");
}
Console.WriteLine();

Console.WriteLine($"PROJECTED ({projectedView.Count} entries — applyCompaction: true, LLM-facing):");
foreach (var entry in projectedView)
{
    var kind = entry switch
    {
        CompactionMarkerEntry m => $"MARKER — \"{Truncate(m.Messages.FirstOrDefault()?.Text ?? string.Empty, 60)}\"",
        _ => entry.GetType().Name,
    };
    Console.WriteLine($"  • {entry.CorrelationId[..Math.Min(20, entry.CorrelationId.Length)]}  {kind}");
}
Console.WriteLine();

// ── Step 8: Demonstrate GDPR erasure cascade ─────────────────────────────────
// Pick the first non-marker entry and erase it via the cascade-aware helper. Note
// what happens to any marker referencing it: tombstone (all refs gone) or
// regenerate (surviving subset, summary cleared).
var firstSource = auditView.FirstOrDefault(e => e is not CompactionMarkerEntry);
if (firstSource is not null)
{
    Console.WriteLine("════════════════════════════════════════════════════════════════════");
    Console.WriteLine(" GDPR erasure cascade demo");
    Console.WriteLine("════════════════════════════════════════════════════════════════════\n");
    Console.WriteLine($"Erasing entry: {firstSource.CorrelationId}\n");

    var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
        store,
        sessionId,
        new HashSet<string> { firstSource.CorrelationId });

    Console.WriteLine($"  MarkersAffected     : {result.MarkersAffected}");
    Console.WriteLine($"  MarkersTombstoned   : {result.MarkersTombstoned}");
    Console.WriteLine($"  MarkersRegenerated  : {result.MarkersRegenerated}");
    Console.WriteLine($"  RemainingMessageCount: {result.RemainingMessageCount}");
    Console.WriteLine();

    var postErase = store.SnapshotRaw(sessionId);
    Console.WriteLine($"Audit canonical after erase: {postErase.Count} entries");
    Console.WriteLine($"  (verify erased entry is gone: {!postErase.Any(e => e.CorrelationId == firstSource.CorrelationId)})");
}

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("\nDone.");

// =============================================================================
// Helpers
// =============================================================================

static string Truncate(string text, int max) =>
    text.Length <= max ? text : text[..max].TrimEnd() + "…";
