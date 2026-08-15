// OpenTelemetry sample — demonstrates how to configure distributed tracing for
// TemporalCommunity.Extensions.AI, showing the full span hierarchy produced by a
// durable chat session.
// Run:  dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj

#pragma warning disable TAI001 // Opt in to the experimental plugin surface (DurableAIPlugin, AddWorkerPlugin)

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Temporalio.Client;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.OpenTelemetry;

// Enable the OpenAI .NET SDK's experimental OpenTelemetry instrumentation.
// Without this switch, AddSource("OpenAI.*") in the tracing config below matches
// nothing emitted by the OpenAI client — verified at
// https://github.com/openai/openai-dotnet/blob/main/docs/Observability.md
AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Quiet down the host lifetime/info categories so the console exporter's span
// output isn't interleaved with "Application started." / "Hosting environment:"
// log lines. Spans dominate the console; lifecycle events still show on error.
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Error);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException(
        "OPENAI_API_BASE_URL is not configured. Set it in appsettings.json, " +
        "as an environment variable, or via " +
        "`dotnet user-secrets set OPENAI_API_BASE_URL https://api.openai.com/v1 --project samples/MEAI/OpenTelemetry`.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/OpenTelemetry");

// ── Setup: Register OpenTelemetry ─────────────────────────────────────────────

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(TracingInterceptor.ClientSource.Name)
        .AddSource(TracingInterceptor.WorkflowsSource.Name)
        .AddSource(TracingInterceptor.ActivitiesSource.Name)
        .AddSource("OpenAI.*")
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        // Production OTLP exporter — uncomment when ready to ship traces to Jaeger / Tempo /
        // Honeycomb / Datadog / Grafana Cloud. Reads OTEL_EXPORTER_OTLP_ENDPOINT automatically
        // (default http://localhost:4317 for gRPC, http://localhost:4318 for HTTP/protobuf).
        // SECURITY: requires https:// in production, plus OTEL_EXPORTER_OTLP_HEADERS for SaaS
        // backend auth — set via secrets manager, never plaintext env var (leaks via /proc,
        // container metadata, crash dumps). See README "Going to Production" section.
        // .AddOtlpExporter()
        )
    .WithMetrics(metrics => metrics
        .AddMeter(DurableChatTelemetry.MeterName)
        .AddConsoleExporter());

// ── Setup: Connect Temporal client with TracingInterceptor + DurableAIDataConverter
//
// TWO things are configured here and both are required:
//
//   TracingInterceptor  — propagates the W3C trace context (traceparent header)
//   from the client into the workflow and from the workflow into each activity.
//   Without it, Temporal's internal gRPC calls break the distributed trace and
//   the spans from the library appear disconnected in your backend.
//
//   DurableAIDataConverter.Instance  — wraps Temporal's payload converter with
//   AIJsonUtilities.DefaultOptions, which preserves the $type discriminator that
//   MEAI uses for polymorphic AIContent subclasses (TextContent, FunctionCallContent,
//   etc.). Without it, type information is silently lost when types round-trip
//   through workflow history, causing deserialization errors on replay.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Interceptors = [new TracingInterceptor()],
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddChatClient is the idiomatic MEAI DI pattern — it returns a ChatClientBuilder
// for chaining middleware, then Build() registers the final IChatClient singleton.
// DurableChatActivities constructor-injects the unkeyed IChatClient on the worker
// side; this is the client it calls when executing the GetChatStep activity
// (which produces the leaf `chat {modelId}` span).
IChatClient openAiChatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model).AsIChatClient();

// This sample registers no tools, so we deliberately do NOT chain
// .UseFunctionInvocation() — managed durable sessions own function invocation.
// To see per-tool spans, look at samples/MEAI/DurableTools or samples/MEAI/DurableChat.
builder.Services.AddChatClient(openAiChatClient);

// ── Setup: Register worker + durable AI via the plugin path ─────────────────
// AddWorkerPlugin(DurableAIPlugin) is the canonical pattern for AI integrations.
// It registers DurableChatWorkflow, DurableChatActivities,
// DurableFunctionActivities, DurableEmbeddingActivities, the function registry,
// DurableChatSessionClient, the DurableExecutionOptions singleton, and queues
// DurableAIPlugin in the worker plugin chain — equivalent to AddDurableAI().
builder.Services
    .AddHostedTemporalWorker("durable-chat-otel")
    .AddWorkerPlugin(new DurableAIPlugin(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        // Demo-friendly TTL (default is 14 days). The sample finishes in seconds;
        // long TTLs only matter for production sessions that may sit idle between turns.
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
        // Without this, RetryPolicy is null and Temporal applies its built-in
        // "retry forever" default — a footgun for transient failures. LLM
        // activities are generally idempotent, so 3 attempts is a sensible cap.
        opts.RetryPolicy = new RetryPolicy { MaximumAttempts = 3 };
    }));

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. OpenTelemetry console exporter is active.\n");
Console.WriteLine("--- spans ---\n");

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// Track every conversation we start so we can signal Shutdown to each running
// workflow before the host exits (see "Shutdown" block below).
var conversationIds = new List<string>();

// ── Run multi-turn conversation ───────────────────────────────────────────────
conversationIds.AddRange(await RunMultiTurnDemoAsync(sessionClient));

// ── Shutdown ──────────────────────────────────────────────────────────────────
// Each demo starts a Temporal workflow that survives host.StopAsync() — the host
// only stops the worker process, not the workflows running on the Temporal
// server. Without an explicit Shutdown signal, those workflows sit parked for
// SessionTimeToLive (1h above) burning workflow slots and cluttering the UI on
// re-runs. Signal each one so DurableChatWorkflowBase.RequestShutdownAsync
// triggers a clean completion of the workflow loop.
foreach (var conversationId in conversationIds)
{
    try
    {
        await sessionClient.ShutdownAsync(conversationId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($" [shutdown] Failed to signal {conversationId}: {ex.Message}");
    }
}

try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ═════════════════════════════════════════════════════════════════════════════
// Multi-turn conversation demo
//
// This demo issues two chat turns in a single conversation. Look at the console
// exporter output above (or below) this block for the span hierarchy. Each call
// to SendAsync produces:
//
//   durable_chat.send (conversation.id = <id>)
//     UpdateWorkflow:Chat
//       RunActivity:GetChatStep
//         chat {modelId} (conversation.id, gen_ai.usage.*)
//
// The conversation.id attribute is the same on both the send and leaf spans,
// making it easy to filter all traces for a single session in your backend.
// ═════════════════════════════════════════════════════════════════════════════
static async Task<IEnumerable<string>> RunMultiTurnDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Multi-Turn Conversation with OpenTelemetry Tracing");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Each conversation maps to a Temporal workflow. Reusing the same ID across
    // SendAsync calls routes all turns to the same workflow instance and keeps
    // the conversation.id attribute consistent across all related spans.
    var conversationId = $"otel-demo-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}");
    Console.WriteLine($" (Search for this ID in the span output below)\n");

    var q1 = "What is the capital of France?";
    Console.WriteLine($" User : {q1}");
    var r1 = await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, q1)]);
    Console.WriteLine($" Agent: {r1.Text}\n");

    // The workflow's history already contains the previous exchange, so the
    // model can answer this pronoun reference without being told explicitly.
    var q2 = "What is the population of that city?";
    Console.WriteLine($" User : {q2}");
    var r2 = await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, q2)]);
    Console.WriteLine($" Agent: {r2.Text}");

    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine(" Check the console exporter output for the span hierarchy:");
    Console.WriteLine("   durable_chat.send");
    Console.WriteLine("     UpdateWorkflow:Chat");
    Console.WriteLine("       RunActivity:GetChatStep");
    Console.WriteLine("         chat {modelId}");
    Console.WriteLine();
    Console.WriteLine($" Filter by tag conversation.id = {conversationId}");
    Console.WriteLine("════════════════════════════════════════════════════════\n");

    return new[] { conversationId };
}
