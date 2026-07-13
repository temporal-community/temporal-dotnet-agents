// HumanInTheLoop — demonstrates a workflow-owned HITL approval gate for a durable tool.
//
// Run:  dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;

// ── Configuration ─────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey        = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl    = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model         = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/HumanInTheLoop");

// ── Temporal client with DurableAIDataConverter ───────────────────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which handles MEAI's $type discriminator for
// polymorphic AIContent subclasses (TextContent, FunctionCallContent, etc.).
// Without this, type information is lost when types round-trip through history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace     = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Register IChatClient ──────────────────────────────────────────────────────
// Managed durable sessions own function invocation, so do not add
// UseFunctionInvocation() to this client pipeline.
IChatClient openAiChatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model).AsIChatClient();

builder.Services.AddChatClient(openAiChatClient);

// This function is registered with the worker. RequireApproval makes the workflow park
// before the InvokeFunction activity is scheduled; the tool itself never calls Temporal APIs.
var deleteTool = AIFunctionFactory.Create(
    async ([Description("Age threshold in days; records older than this will be deleted")] int olderThanDays) =>
    {
        Console.WriteLine($" [Tool] Deleting records older than {olderThanDays} days...");
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // Simulate I/O.
        return $"Successfully deleted all records older than {olderThanDays} days.";
    },
    name: "delete_records",
    description: "Permanently deletes records older than the specified number of days. Requires human approval.");

// ── Worker + durable AI ───────────────────────────────────────────────────────
// The approval wait is workflow-owned: no activity stays open while a reviewer decides.
// ApprovalTimeout must be long enough to cover the full human review window, and
// SessionTimeToLive must outlast that window.
const string taskQueue = "hitl-meai-sample";

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddDurableAI(opts =>
    {
        // The workflow wait is durable and does not consume an activity while a human reviews.
        // SessionTimeToLive must exceed the approval window.
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.HeartbeatTimeout  = TimeSpan.FromMinutes(2);
        opts.ApprovalTimeout   = TimeSpan.FromHours(24);
        opts.SessionTimeToLive = TimeSpan.FromHours(26);
    })
    .AddDurableTools(deleteTool, tool =>
        tool.NoRetry().RequireApproval().WithApprovalTimeout(TimeSpan.FromHours(24)));

// ── Build and start host ──────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   Data Management Assistant — HITL Approval Sample       ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
Console.WriteLine("║  The assistant can delete records but requires approval.  ║");
Console.WriteLine("║  This sample auto-approves to demonstrate the full flow.  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// ── Run the HITL demo ─────────────────────────────────────────────────────────
await RunHitlDemoAsync(sessionClient);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("\nDone.");

// ═════════════════════════════════════════════════════════════════════════════
// HITL Demo
//
// Shows the complete approval gate: the workflow receives a delete_records request,
// parks before dispatching the tool activity, and exposes the pending request. The main loop
// polls GetPendingApprovalAsync, discovers the request, and auto-approves it.
// SendAsync then returns with the LLM's final response.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunHitlDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Human-in-the-Loop Tool Approval");
    Console.WriteLine("════════════════════════════════════════════════════════════");

    // Each conversationId maps to one DurableChatWorkflow instance.
    var conversationId = $"hitl-demo-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    // ── System prompt explaining the assistant's purpose ──────────────────
    var systemMessage = new ChatMessage(ChatRole.System,
        """
        You are a helpful data management assistant.
        You can answer questions about records and data.
        When the user requests a delete operation, use the delete_records tool.
        Always confirm what you are about to delete before calling the tool.
        If a deletion is rejected by the reviewer, explain the situation and offer alternatives.
        """);

    var userQuestion = "Delete all records older than 30 days.";
    Console.WriteLine($" User : {userQuestion}\n");

    // ── Start the chat turn ───────────────────────────────────────────────
    // SendAsync sends a Chat [WorkflowUpdate] to DurableChatWorkflow. The workflow uses the
    // delete_records schema registered with AddDurableTools, parks for approval, then schedules
    // InvokeFunction only after approval is submitted.

    // Start chat in the background — it will block inside the tool waiting for approval.
    // Note: the chat task is NOT awaited here. It runs concurrently so the main thread
    // can poll for the pending approval request and submit a decision.
    var chatTask = sessionClient.SendAsync(
        conversationId,
        [systemMessage, new ChatMessage(ChatRole.User, userQuestion)]);

    Console.WriteLine(" [Main] Chat started — polling for pending approval...\n");

    // ── Poll for the pending approval request ─────────────────────────────
    // GetPendingApprovalAsync is a [WorkflowQuery] — it returns instantly and
    // never blocks the workflow. Poll until the tool has registered its request.
    DurableApprovalRequest? pending = null;
    while (!chatTask.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Check for faults before querying — a faulted task has IsCompleted == true
        // so the while predicate will exit on the next tick, but the misleading
        // "no approval gate" message would print. Surface the fault explicitly here.
        if (chatTask.IsFaulted)
            await chatTask; // rethrows the underlying exception

        try
        {
            pending = await sessionClient.GetPendingApprovalAsync(conversationId);
        }
        catch (Temporalio.Exceptions.RpcException ex)
            when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            // Workflow may not have registered its first event yet on the very
            // first poll. Retry on the next tick.
            continue;
        }

        if (pending is not null) break;
    }

    if (pending is not null)
    {
        // ── Display the pending approval request ──────────────────────────
        Console.WriteLine(" ╔══════════════════════════════════════════════════╗");
        Console.WriteLine(" ║           APPROVAL REQUIRED                      ║");
        Console.WriteLine(" ╠══════════════════════════════════════════════════╣");
        Console.WriteLine($" ║  Request ID  : {pending.RequestId[..8]}...                       ║");
        var fnName = pending.FunctionName ?? string.Empty;
        Console.WriteLine($" ║  Function    : {fnName[..Math.Min(34, fnName.Length)],-34}║");
        if (pending.Description is { Length: > 0 })
            Console.WriteLine($" ║  Description : {pending.Description[..Math.Min(34, pending.Description.Length)],-34}║");
        Console.WriteLine(" ╚══════════════════════════════════════════════════╝");
        Console.WriteLine();

        // ── Auto-approve (simulating a human reviewer) ────────────────────
        // In a real system this would be replaced by:
        //   • Console.ReadLine() to capture input
        //   • A webhook/Slack handler
        //   • Any external decision mechanism
        Console.WriteLine(" [Reviewer] Auto-approving request to demonstrate the full flow...");

        // ResolveApprovalAsync sends the retry-safe ResolveApproval [WorkflowUpdate].
        // This satisfies the workflow's durable approval wait. The workflow then schedules
        // the delete_records activity and resumes the model/tool loop.
        var decision = new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved  = true,
            Reason    = "Auto-approved by sample reviewer.",
        };

        await sessionClient.ResolveApprovalAsync(conversationId, decision);
        Console.WriteLine(" [Reviewer] Approval submitted — waiting for assistant response...\n");
    }
    else
    {
        Console.WriteLine(" [Main] Chat completed without triggering an approval gate.\n");
    }

    // ── Await the final response ──────────────────────────────────────────
    // Now that the approval has been submitted, the workflow runs the tool activity, sends its
    // result to the next model step, and SendAsync returns the final response.
    var response = await chatTask;
    Console.WriteLine($" Assistant: {response.Text}");
    Console.WriteLine();

    // ── Show persisted history ────────────────────────────────────────────
    // GetHistoryAsync retrieves the full conversation log from the workflow.
    // Returns IReadOnlyList<DurableSessionEntry> — each entry is a request or
    // response carrying its own messages plus per-turn metadata (CorrelationId,
    // Usage, CreatedAt). Flatten to count individual messages (user, assistant,
    // tool calls, tool results).
    var history = await sessionClient.GetHistoryAsync(conversationId);
    var messageCount = history.Sum(e => e.Messages.Count);
    Console.WriteLine($" [History] {messageCount} messages persisted in workflow state.");

    Console.WriteLine("════════════════════════════════════════════════════════════\n");
}
