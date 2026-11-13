// AmbientAgent — a background monitoring workflow that ingests health-check signals,
// calls an LLM to detect anomalies, and cross-signals an alert workflow on findings.
//
// Run:  dotnet run --project samples/MAF/AmbientAgent/AmbientAgent.csproj

using System.ClientModel;
using AmbientAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/AmbientAgent");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register the Temporal client (AlertActivities needs ITemporalClient) ─
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalAddress;
    opts.Namespace = "default";
});

// ── Step 5: Register the hosted worker with both ambient agents ──────────────
builder.Services
    .AddHostedTemporalWorker("ambient-agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("AnalysisAgent", agent =>
        {
            agent.Instructions =
                "You are a system health analyst. You receive time-series health metrics " +
                "(CPU%, Memory%, Temperature) and must assess whether the readings are normal or anomalous. " +
                "For this demo, a reading is anomalous if CPU or Memory is at least 90%, " +
                "or Temperature is at least 85 degrees Celsius. Lower values are normal. " +
                "Respond with 'NORMAL: <brief summary>' if every reading is normal, " +
                "or 'ANOMALY: <description of the concerning pattern>' if any reading " +
                "crosses a threshold. Be concise.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });

        opts.AddDurableAgent("AlertAgent", agent =>
        {
            agent.Instructions =
                "You are an alert notification composer. Given anomaly details and recent system " +
                "readings, compose a concise, actionable alert notification suitable for an operations " +
                "team. Include: severity assessment, affected metrics, and recommended next steps. " +
                "Keep it under 100 words.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });
    })
    .AddWorkflow<MonitorWorkflow>()
    .AddWorkflow<AlertWorkflow>()
    .AddSingletonActivities<AlertActivities>()
    .AddScopedActivities<AgentInvocationActivities>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Launching ambient agent workflows...\n");

var client = host.Services.GetRequiredService<ITemporalClient>();

var runId = Guid.NewGuid().ToString("N");
var monitorWorkflowId = $"ambient-monitor-{runId}";
var alertWorkflowId = $"ambient-alert-{runId}";

// ── Step 7: Start the AlertWorkflow (must exist before MonitorWorkflow signals it) ─
await client.StartWorkflowAsync(
    (AlertWorkflow wf) => wf.RunAsync(),
    new WorkflowOptions
    {
        Id = alertWorkflowId,
        TaskQueue = "ambient-agents",
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
    });

Console.WriteLine($"AlertWorkflow started: {alertWorkflowId}");

// ── Step 8: Start the MonitorWorkflow ────────────────────────────────────────
var monitorHandle = await client.StartWorkflowAsync(
    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
    {
        AlertWorkflowId = alertWorkflowId,
        AnalysisInterval = 5,
        MaxBufferSize = 50
    }),
    new WorkflowOptions
    {
        Id = monitorWorkflowId,
        TaskQueue = "ambient-agents",
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
    });

Console.WriteLine($"MonitorWorkflow started: {monitorWorkflowId}\n");

// ── Step 9: Simulate 20 health-check readings ────────────────────────────────
// Normal readings for most, with a spike window at readings 13-15.
Console.WriteLine("── Sending simulated health readings ───────────────────────");

var random = new Random(42); // Deterministic seed for reproducibility
var now = DateTimeOffset.UtcNow;

for (int i = 1; i <= 20; i++)
{
    double cpu, memory, temp;

    if (i is >= 13 and <= 15)
    {
        // Spike window — anomalous readings
        cpu = 95 + random.NextDouble() * 5;       // 95-100%
        memory = 96 + random.NextDouble() * 4;     // 96-100%
        temp = 88 + random.NextDouble() * 7;       // 88-95°C
    }
    else
    {
        // Normal readings
        cpu = 20 + random.NextDouble() * 40;       // 20-60%
        memory = 30 + random.NextDouble() * 35;    // 30-65%
        temp = 45 + random.NextDouble() * 20;      // 45-65°C
    }

    var reading = new HealthCheckData(
        Timestamp: now.AddSeconds(i * 10),
        CpuPercent: Math.Round(cpu, 1),
        MemoryPercent: Math.Round(memory, 1),
        TemperatureCelsius: Math.Round(temp, 1));

    await monitorHandle.SignalAsync(wf => wf.IngestHealthCheckAsync(reading));
    Console.WriteLine($"  Reading {i,2}: CPU={reading.CpuPercent,5:F1}% Mem={reading.MemoryPercent,5:F1}% Temp={reading.TemperatureCelsius,5:F1}°C{(i is >= 13 and <= 15 ? " ⚠️ SPIKE" : "")}");

    // Rate-limit to simulate real sensor cadence — signals are delivered asynchronously,
    // so this delay is not a synchronization barrier.
    await Task.Delay(200);
}

// ── Step 10: Drain monitor analyses and report final status ──────────────────
Console.WriteLine("\nDraining complete analysis batches...\n");
await monitorHandle.SignalAsync(wf => wf.ShutdownAsync());
await monitorHandle.GetResultAsync();

Console.WriteLine("── Monitor Status ──────────────────────────────────────────");
var status = await monitorHandle.QueryAsync(wf => wf.GetStatus());
Console.WriteLine($"  Buffer size: {status.BufferSize}");
Console.WriteLine($"  Total readings: {status.TotalReadings}");
Console.WriteLine($"  Analyses performed: {status.RecentAnalyses.Count}");
Console.WriteLine($"  Pending partial readings: {status.PendingAnalysisReadings}");
foreach (var analysis in status.RecentAnalyses)
{
    Console.WriteLine($"\n  Analysis: {analysis}");
}

string[] expectedAnalysisPrefixes = ["NORMAL:", "NORMAL:", "ANOMALY:", "NORMAL:"];
if (status.TotalReadings != 20
    || status.BufferSize != 20
    || status.PendingAnalysisReadings != 0
    || status.RecentAnalyses.Count != expectedAnalysisPrefixes.Length
    || !status.RecentAnalyses.Zip(
        expectedAnalysisPrefixes,
        (analysis, prefix) => analysis.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).All(matches => matches))
{
    Console.Error.WriteLine(
        "Expected all 20 readings to drain into four batches: NORMAL, NORMAL, ANOMALY, NORMAL.");
    Environment.ExitCode = 1;
}

// ── Step 11: Drain alert notifications ──────────────────────────────────────
Console.WriteLine("\n── Alert Notifications ─────────────────────────────────────");
var alertHandle = client.GetWorkflowHandle<AlertWorkflow>(alertWorkflowId);
await alertHandle.SignalAsync(wf => wf.ShutdownAsync());
await alertHandle.GetResultAsync();
var notifications = await alertHandle.QueryAsync(wf => wf.GetNotifications());

if (notifications.Count == 0)
{
    Console.WriteLine("  (no anomalies detected)");
}
else
{
    foreach (var notification in notifications)
    {
        Console.WriteLine($"\n  {notification}");
    }
}

if (notifications.Count != 1
    || string.IsNullOrWhiteSpace(notifications[0])
    || notifications[0] == "(no notification text)")
{
    Console.Error.WriteLine("Expected exactly one nonempty notification for the spike batch.");
    Environment.ExitCode = 1;
}

// ── Step 12: Stop the worker after both workflows have drained ───────────────
Console.WriteLine("\n── Shutting down ───────────────────────────────────────────");

await host.StopAsync();
Console.WriteLine("All workflows drained.");
Console.WriteLine("Done.");
if (Environment.ExitCode == 0)
    Console.WriteLine("AmbientAgent sample complete.");
