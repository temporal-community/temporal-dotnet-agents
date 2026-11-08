// PerToolActivities — demonstrates serial Temporal tool activity dispatch.
//
// Each LLM call is a RunDurableAgentStep activity; each tool call is an
// InvokeAgentTool:<tool_name> activity, serially in model order. Tool activities
// use one attempt by default; retries are an explicit per-agent opt-in.
//
// Run:  dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj
//
// Five scenarios run back-to-back:
//   1. Happy path — all tool activities complete successfully.
//   2. Subset — only lookup_order is exposed and dispatchable.
//   3. Empty list — no tools are exposed or dispatchable.
//   4. Disabled — EnableToolCalls=false blocks every model-returned call.
//   5. Lookup failure — it gets one attempt by default, and later calls do not run.

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using PerToolActivities;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("TemporalCommunity.Extensions.Agents", LogLevel.Information);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/PerToolActivities");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
const string taskQueue = "per-tool-activities";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register tool services as singletons ─────────────────────────────
// The same instance the AIFunction factories bind to is the instance the demo
// driver inspects after each scenario to print LookupCalls / RefundCalls / SendCalls.
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient. The durable-agent path composes its pipeline
// internally with UseProvidedChatClientAsIs = true, so MAF's auto-injected
// FunctionInvokingChatClient cannot intercept tool calls — the workflow owns
// the tool-dispatch loop and dispatches each tool as a separate Temporal activity.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register Temporal client + worker pipeline ───────────────────────
// AddDurableAgent is the single registration entry point:
//   • agent.AddTool(...) — every tool gets one activity attempt by default.
// Tool retries are an explicit per-agent opt-in and apply uniformly.
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Description = "Customer refund specialist with read and write tools.";
            agent.Instructions =
                "You are a customer refund specialist. Use lookup_order to verify the order, " +
                "apply_refund to issue the refund, and send_email to confirm with the customer. " +
                "Always look up the order before applying a refund. After applying the refund, " +
                "send a single confirmation email and produce a brief final summary for the user.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 10;

            // Read tool — one activity attempt by default.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                name: "lookup_order",
                description: "Look up the current status of a customer order by order ID."));

            // Write tools — one activity attempt by default. Per-tool timeouts
            // remain configurable; retry policy is agent-level only.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    name: "apply_refund",
                    description: "Apply a refund of the specified amount to the order. WRITE — non-idempotent."),
                opts => opts.WithTimeout(TimeSpan.FromSeconds(30)));

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    name: "send_email",
                    description: "Send an email to the customer. WRITE — non-idempotent."),
                opts => opts.WithTimeout(TimeSpan.FromSeconds(30)));
        });
    })
    .AddWorkflow<RefundWorkflow>()
    .AddScopedActivities<RefundAgentActivities>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running serial tool activity scenarios...\n");

var client = host.Services.GetRequiredService<ITemporalClient>();
var orderService = host.Services.GetRequiredService<OrderService>();
var refundService = host.Services.GetRequiredService<RefundService>();
var emailService = host.Services.GetRequiredService<EmailService>();

// ── Step 6a: Scenario 1 — happy path ─────────────────────────────────────────
Console.WriteLine("─── Scenario 1: Happy path (all tools succeed) ──────────────");
orderService.FailOnceEnabled = false;

await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-happy-{Guid.NewGuid():N}",
    complaint: "I never received my order ORD-002 and want a full $49.99 refund. " +
               "Please email me at acme@example.com once it's processed.");

PrintToolStats(orderService, refundService, emailService);
Console.WriteLine();

// ── Step 6b: Per-turn tool-selection matrix ──────────────────────────────────
Console.WriteLine("─── Scenario 2: Subset (lookup_order only) ────────────────");
await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-subset-{Guid.NewGuid():N}",
    complaint: "Only look up the current status of order ORD-003. Do not refund or email.",
    enableToolNames: ["LOOKUP_ORDER"]);
Console.WriteLine();

Console.WriteLine("─── Scenario 3: Empty enabled-name list (no tools) ─────────");
await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-empty-{Guid.NewGuid():N}",
    complaint: "Explain that no operational tools are available for this turn.",
    enableToolNames: []);
Console.WriteLine();

Console.WriteLine("─── Scenario 4: Tool calls disabled ────────────────────────");
await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-disabled-{Guid.NewGuid():N}",
    complaint: "Explain that tool execution is disabled for this turn.",
    enableToolCalls: false);
Console.WriteLine();

// ── Step 6c: Scenario 5 — lookup failure ─────────────────────────────────────
var lookupBefore = orderService.LookupCalls;
var refundBefore = refundService.RefundCalls;
var emailBefore = emailService.SendCalls;

Console.WriteLine("─── Scenario 5: Lookup failure (one attempt by default) ─────");
orderService.FailOnceEnabled = true;

await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-retry-{Guid.NewGuid():N}",
    complaint: "Refund my order ORD-001 for $19.99. Email confirmation to acme@example.com.");

var lookupDelta = orderService.LookupCalls - lookupBefore;
var refundDelta = refundService.RefundCalls - refundBefore;
var emailDelta = emailService.SendCalls - emailBefore;

Console.WriteLine($"  This run    → LookupCalls = {lookupDelta}, RefundCalls = {refundDelta}, SendCalls = {emailDelta}");
Console.WriteLine($"  Cumulative  → LookupCalls = {orderService.LookupCalls}, " +
                  $"RefundCalls = {refundService.RefundCalls}, SendCalls = {emailService.SendCalls}");

if (lookupDelta == 1 && refundDelta == 0 && emailDelta == 0)
{
    Console.WriteLine("  ✓ Default policy confirmed: lookup ran once; later tools were not dispatched after its failure.");
}
Console.WriteLine();

// ── Step 7: Operator guidance + graceful shutdown ────────────────────────────
Console.WriteLine("─── View the activity timeline ──────────────────────────────");
Console.WriteLine("  Open http://localhost:8233 in the Temporal Web UI.");
Console.WriteLine("  Each workflow above shows distinct activity rows:");
Console.WriteLine("    • RunDurableAgentStep                        — one row per LLM call");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:lookup_order   — one attempt by default");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:apply_refund   — one attempt by default");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:send_email     — one attempt by default");
Console.WriteLine();

try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// LOCAL HELPERS
// ─────────────────────────────────────────────────────────────────────────────

static async Task RunScenarioAsync(
    ITemporalClient client,
    string taskQueue,
    string workflowId,
    string complaint,
    bool enableToolCalls = true,
    IReadOnlyList<string>? enableToolNames = null)
{
    Console.WriteLine($"  Workflow:  {workflowId}");
    Console.WriteLine($"  Complaint: {complaint}");
    Console.WriteLine($"  Selection: EnableToolCalls={enableToolCalls}, EnableToolNames=" +
        (enableToolNames is null ? "<all registered>" : $"[{string.Join(", ", enableToolNames)}]"));

    try
    {
        var result = await client.ExecuteWorkflowAsync(
            (RefundWorkflow wf) => wf.RunAsync(new RefundTurnInput(
                complaint,
                enableToolCalls,
                enableToolNames)),
            new WorkflowOptions
            {
                Id = workflowId,
                TaskQueue = taskQueue,
            });

        Console.WriteLine($"  Agent:     {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Workflow failed: {ex.Message}");
    }
}

static void PrintToolStats(OrderService orders, RefundService refunds, EmailService emails)
{
    Console.WriteLine($"  Final state → LookupCalls = {orders.LookupCalls}, " +
                      $"RefundCalls = {refunds.RefundCalls}, SendCalls = {emails.SendCalls}");
}
