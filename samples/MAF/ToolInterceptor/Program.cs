// ToolInterceptor — demonstrates IAgentToolInterceptor (Feature L) and
// workflow-parked HITL approval via RequireApproval() (Feature A).
//
// Scenario: customer refund agent. Three turns exercise all four interceptor
// decision paths in a single coherent flow:
//
//   Turn 1 — nonexistent order  → Skip  (synthetic result; tool never dispatches)
//   Turn 2 — large refund $750  → Block (policy violation; tool never dispatches)
//   Turn 3 — valid refund $29.99 → PauseForApproval (auto-approved in the demo loop)
//
// Run:  dotnet run --project samples/MAF/ToolInterceptor/ToolInterceptor.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using ToolInterceptor;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.AI;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ToolInterceptor");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register service singletons ──────────────────────────────────────
// Both are singletons so the interceptor and the tool factories resolve the same instance.
builder.Services.AddSingleton<FakeOrderService>();
builder.Services.AddSingleton<OrderInterceptor>();

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient — the durable-agent path composes its own pipeline
// internally. Calling .UseFunctionInvocation() here would short-circuit Temporal's
// per-tool activity dispatch.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the durable agent ───────────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("tool-interceptor-sample")
    .AddTemporalAgents(opts =>
    {
        // HITL requires timeouts that cover the full human review window.
        // In production, set DefaultActivityTimeout and DefaultApprovalTimeout
        // to accommodate the longest expected human response time (e.g. 24 h).
        // Here we use short values so the demo exits cleanly.
        opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(2);  // production: hours
        opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(1);  // must be < DefaultActivityTimeout

        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Instructions =
                "You are a customer refund specialist. Help customers get refunds for their orders. " +
                "Always look up an order before applying a refund. " +
                "Report clearly what happened after each tool call.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromMinutes(10); // shortened for demo

            // Per-agent interceptor — fires before every non-opted-out tool call.
            agent.AddToolInterceptor(sp => sp.GetRequiredService<OrderInterceptor>());

            // lookup_order: read-only, no interceptor overhead needed.
            agent.AddTool(
                "lookup_order",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<FakeOrderService>().LookupOrder,
                    name: "lookup_order",
                    description: "Look up an order by its order ID and return a summary of its details."),
                opts => opts.SkipInterceptor());

            // apply_refund: write tool — NoRetry() prevents double-execution on retry.
            // RequireApproval() is the Rule 2 absolute floor: even if the interceptor
            // somehow returns Proceed, the turn loop still parks for human approval.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<FakeOrderService>().ApplyRefund,
                    name: "apply_refund",
                    description: "Apply a refund of the specified amount to the given order. WRITE — non-idempotent."),
                opts => opts.NoRetry().RequireApproval());
        });
    });

// ── Step 6: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running three-turn refund scenario...\n");

var proxy = host.Services.GetTemporalAgentProxy("RefundAgent");
var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

// One shared session across all three turns so conversation history is preserved.
var session = await proxy.CreateSessionAsync();
if (session is not TemporalAgentSession temporalSession)
    throw new InvalidOperationException("Expected a TemporalAgentSession.");
var sessionId = temporalSession.SessionId;

// ── Turn 1: Nonexistent order — interceptor Skip ──────────────────────────────
Console.WriteLine("=== Turn 1: Nonexistent order ===");
Console.WriteLine("User : I need a refund for order ORD-999");
var r1 = await proxy.RunAsync("I need a refund for order ORD-999", session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}");
Console.WriteLine();

// ── Turn 2: Large refund — interceptor Block ──────────────────────────────────
Console.WriteLine("=== Turn 2: Large refund (blocked) ===");
Console.WriteLine("User : What about order ORD-002? I want a full refund.");
var r2 = await proxy.RunAsync("What about order ORD-002? I want a full refund.", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}");
Console.WriteLine();

// ── Turn 3: Valid refund — interceptor PauseForApproval ───────────────────────
Console.WriteLine("=== Turn 3: Valid refund (approval required) ===");
Console.WriteLine("User : Please refund $29.99 for order ORD-001");

// Start the agent call without awaiting — it will park inside the tool while
// waiting for approval. We poll GetPendingApprovalAsync and auto-approve.
var agentTask = proxy.RunAsync("Please refund $29.99 for order ORD-001", session);

// Poll for a pending approval. GetPendingApprovalAsync is a WorkflowQuery —
// it never blocks and is safe to call as frequently as needed.
DurableApprovalRequest? pending = null;
while (!agentTask.IsCompleted)
{
    await Task.Delay(TimeSpan.FromMilliseconds(500));
    if (agentTask.IsCompleted) break;

    try
    {
        pending = await agentClient.GetPendingApprovalAsync(sessionId);
    }
    catch (Temporalio.Exceptions.RpcException ex)
        when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
    {
        // Workflow may not have started yet on the very first poll — retry.
        continue;
    }

    if (pending is null) continue;

    // Approval gate — print the enriched description and auto-approve for demo.
    Console.WriteLine($"[Approval requested] {pending.Description}");
    Console.WriteLine("[Auto-approving for demo]");

    await agentClient.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Demo auto-approve",
    });

    // Break so we don't loop and re-approve after the tool has already run.
    break;
}

var r3 = await agentTask;
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}");
Console.WriteLine();

// ── Step 7: Graceful shutdown ─────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }

Console.WriteLine("Done.");
