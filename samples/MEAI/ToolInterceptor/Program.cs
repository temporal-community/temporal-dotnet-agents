// ToolInterceptor — demonstrates IDurableToolInterceptor<DurableToolContext> in a MEAI
// durable chat session. The AuditInterceptor fires before each tool activity and returns
// one of four outcomes: Proceed, Block, Skip, or PauseForApproval.
//
// Scenario: a simple "file assistant" with read_file and delete_file tools.
//   Turn 1 — read config.json     → interceptor skipped (SkipInterceptor() on read_file)
//   Turn 2 — delete system.lock   → Block (protected file)
//   Turn 3 — delete config.json   → PauseForApproval; program auto-approves to show full flow
//
// Run:  dotnet run --project samples/MEAI/ToolInterceptor/ToolInterceptor.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Tools;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey          = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl      = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model           = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

// Configuration values may come from any of:
//   - appsettings.json (committed defaults — fine for OPENAI_API_BASE_URL, OPENAI_MODEL, TEMPORAL_ADDRESS)
//   - environment variables (always loaded by Host.CreateApplicationBuilder)
//   - user secrets (loaded only in Development environment; csproj declares <UserSecretsId>)
// OPENAI_API_KEY is sensitive — keep it in user secrets or env vars, never in appsettings.json.
if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException(
        "OPENAI_API_BASE_URL is not configured. Set it in appsettings.json, " +
        "as an environment variable, or via " +
        "`dotnet user-secrets set OPENAI_API_BASE_URL https://api.openai.com/v1 --project samples/MEAI/ToolInterceptor`.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it as an environment variable or via " +
        "`dotnet user-secrets set OPENAI_API_KEY sk-... --project samples/MEAI/ToolInterceptor`. " +
        "Note: user secrets only load in the Development environment (DOTNET_ENVIRONMENT unset or set to 'Development').");

const string TaskQueue = "tool-interceptor-meai";

// ── Setup: FakeFileSystem and tool functions ──────────────────────────────────
// FakeFileSystem holds the in-memory files. The tool implementations (ReadFile,
// DeleteFile) are plain methods — registering them via AddDurableTool() below
// is what makes each call run as a separate Temporal activity in the managed tool loop.
var fs = new FakeFileSystem();

var readFileTool = AIFunctionFactory.Create(
    fs.ReadFile,
    name: "read_file",
    description: "Read the contents of a file by name.");

var deleteFileTool = AIFunctionFactory.Create(
    fs.DeleteFile,
    name: "delete_file",
    description: "Permanently delete a file by name. This operation cannot be undone.");

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// We do NOT call .UseFunctionInvocation(): AddDurableTools below supplies the worker
// registry and the workflow owns the tool-dispatch loop.
IChatClient openAiChatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model).AsIChatClient();

builder.Services.AddChatClient(openAiChatClient);

// ── Setup: Register interceptor and file system in DI ─────────────────────────
// Register the interceptor both as its concrete type and as the base interface so
// DurableChatActivities.RunToolInterceptorAsync can resolve it from DI.
builder.Services.AddSingleton<AuditInterceptor>();
builder.Services.AddSingleton<IDurableToolInterceptor<DurableToolContext>>(
    sp => sp.GetRequiredService<AuditInterceptor>());
builder.Services.AddSingleton(fs);

// ── Setup: Connect Temporal client with DurableAIDataConverter ───────────────
// DurableAIDataConverter.Instance wraps the payload converter with
// AIJsonUtilities.DefaultOptions so MEAI's polymorphic AIContent subclasses
// ($type discriminators) survive round-trips through Temporal history.
// Registered as ITemporalClient so it can be resolved for shutdown signals.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// DefaultToolInterceptor wires AuditInterceptor into the managed tool-dispatch loop.
// Before each tool activity, RunToolInterceptor fires AuditInterceptor.BeforeToolCallAsync.
// The returned DurableToolDecision controls whether the tool proceeds, is blocked,
// skips with a synthetic result, or pauses for human approval.
//
// ApprovalTimeout must cover the full human review window. Turn 3 auto-approves
// in milliseconds, so the default 7-day window is intentionally generous here.
var workerBuilder = builder.Services
    .AddHostedTemporalWorker(TaskQueue)
    .AddDurableAI(opts =>
    {
        opts.SessionTimeToLive = TimeSpan.FromMinutes(10);
        opts.RetryPolicy = new RetryPolicy { MaximumAttempts = 3 };

        // Register the interceptor. Resolved from DI by the worker — the factory
        // is called once when the DurableChatSessionClient first needs ActivityOptions.
        opts.DefaultToolInterceptor = sp => sp.GetRequiredService<AuditInterceptor>();
    });

// ── Tool registration: read_file with SkipInterceptor() ──────────────────────
// SkipInterceptor() opts this tool out of RunToolInterceptor entirely — the
// interceptor activity is not dispatched and read_file proceeds directly to
// InvokeFunction. Appropriate for read-only tools where policy evaluation adds
// no value. The tool still runs as its own Temporal activity.
workerBuilder.AddDurableTool(readFileTool, opts => opts.SkipInterceptor());

// ── Tool registration: delete_file with RequireApproval() + NoRetry() ────────
// RequireApproval() is the Rule 2 absolute floor: even if the interceptor returns
// Proceed, the dispatch loop pauses for human approval. In this sample the
// interceptor returns PauseForApproval for non-protected files, so both mechanisms
// agree — the Rule 2 floor is redundant here but shown for completeness.
//
// NoRetry() prevents double-execution: if the activity fails after the file has
// already been deleted, a retry would silently succeed on a non-existent file.
// Write-style tools should always use NoRetry().
workerBuilder.AddDurableTool(deleteFileTool, opts => opts.NoRetry().RequireApproval());

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
// temporalClient was connected and registered above; resolve it for workflow signals.

// Track conversation IDs so we can signal Shutdown before the host exits.
var conversationIds = new List<string>();

// ── System prompt ─────────────────────────────────────────────────────────────
var systemMessage = new ChatMessage(ChatRole.System,
    """
    You are a helpful file management assistant.
    You can read files with read_file and delete files with delete_file.
    Always use the exact file name the user provides.
    If a file operation is blocked or rejected, explain what happened.
    """);

// ── Turn 1: Read config.json ──────────────────────────────────────────────────
// read_file has SkipInterceptor() — the interceptor activity is not dispatched.
// The tool runs directly as an InvokeFunction activity.
Console.WriteLine("════════════════════════════════════════════════════════");
Console.WriteLine(" Turn 1: Read a file (interceptor skipped via SkipInterceptor)");
Console.WriteLine("════════════════════════════════════════════════════════");

var convId1 = $"interceptor-read-{Guid.NewGuid():N}";
conversationIds.Add(convId1);

var q1 = "What's in config.json?";
Console.WriteLine($" User : {q1}");

var r1 = await sessionClient.SendAsync(
    convId1,
    [systemMessage, new ChatMessage(ChatRole.User, q1)]);

Console.WriteLine($" Agent: {r1.Text}");
Console.WriteLine(" (Interceptor was skipped — read_file ran directly as an activity)");
Console.WriteLine("════════════════════════════════════════════════════════\n");

// ── Turn 2: Delete system.lock (protected file → Block) ───────────────────────
// The AuditInterceptor returns Block for protected files. The dispatch loop
// injects a FunctionResultContent with the block reason so the LLM can
// explain what happened to the user.
Console.WriteLine("════════════════════════════════════════════════════════");
Console.WriteLine(" Turn 2: Delete a protected file (interceptor → Block)");
Console.WriteLine("════════════════════════════════════════════════════════");

var convId2 = $"interceptor-block-{Guid.NewGuid():N}";
conversationIds.Add(convId2);

var q2 = "Delete system.lock";
Console.WriteLine($" User : {q2}");

var r2 = await sessionClient.SendAsync(
    convId2,
    [systemMessage, new ChatMessage(ChatRole.User, q2)]);

Console.WriteLine($" Agent: {r2.Text}");
Console.WriteLine(" (AuditInterceptor blocked the tool — delete_file was never invoked)");
Console.WriteLine("════════════════════════════════════════════════════════\n");

// ── Turn 3: Delete config.json (PauseForApproval + Rule 2 RequireApproval) ────
// Two gates apply:
//   Gate A — interceptor returns PauseForApproval with an enriched description.
//   Gate B — RequireApproval() on DurableChatToolOptions (Rule 2 absolute floor).
// Both gates agree: a human must approve before delete_file runs.
//
// The chat turn blocks inside the workflow waiting for ResolveApprovalAsync.
// We start it in the background, poll GetPendingApprovalAsync until the request
// appears, print the enriched description, then auto-approve.
Console.WriteLine("════════════════════════════════════════════════════════");
Console.WriteLine(" Turn 3: Delete config.json (PauseForApproval → auto-approve)");
Console.WriteLine("════════════════════════════════════════════════════════");

var convId3 = $"interceptor-approve-{Guid.NewGuid():N}";
conversationIds.Add(convId3);

var q3 = "Delete config.json";
Console.WriteLine($" User : {q3}");
Console.WriteLine(" [Main] Starting chat (will block waiting for approval)...\n");

// Start the chat in the background — it will block inside the workflow once the
// interceptor returns PauseForApproval, waiting for ResolveApprovalAsync.
var chatTask3 = sessionClient.SendAsync(
    convId3,
    [systemMessage, new ChatMessage(ChatRole.User, q3)]);

// Poll GetPendingApprovalAsync every 500 ms until the request appears.
// GetPendingApprovalAsync is a [WorkflowQuery] and never blocks the workflow.
DurableApprovalRequest? pending = null;
while (!chatTask3.IsCompleted)
{
    await Task.Delay(TimeSpan.FromMilliseconds(500));

    // Propagate faults eagerly rather than printing a misleading "no approval" message.
    if (chatTask3.IsFaulted)
        await chatTask3;

    try
    {
        pending = await sessionClient.GetPendingApprovalAsync(convId3);
    }
    catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
    {
        // Workflow may not have started its first event on the very first poll.
        continue;
    }

    if (pending is not null) break;
}

if (pending is not null)
{
    // Display the enriched description the interceptor attached to PauseForApproval.
    Console.WriteLine(" ╔══════════════════════════════════════════════════╗");
    Console.WriteLine(" ║           APPROVAL REQUIRED                      ║");
    Console.WriteLine(" ╠══════════════════════════════════════════════════╣");
    Console.WriteLine($" ║  Request ID  : {pending.RequestId[..8]}...                       ║");
    var fnName = pending.FunctionName ?? string.Empty;
    Console.WriteLine($" ║  Function    : {fnName[..Math.Min(34, fnName.Length)],-34}║");
    if (pending.Description is { Length: > 0 })
        Console.WriteLine($" ║  Description : {pending.Description[..Math.Min(34, pending.Description.Length)],-34}║");
    if (pending.ExpiresAt is { } expiresAt)
        Console.WriteLine($" ║  Expires at  : {expiresAt:O} ║");
    if (pending.ReviewData is not null)
    {
        foreach (var (key, value) in pending.ReviewData)
            Console.WriteLine($" ║  {key,-11}: {value,-34}║");
    }
    Console.WriteLine(" ╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine(" [Reviewer] Auto-approving to demonstrate the full flow...");

    // Auto-approve — simulating a human reviewer confirming the operation.
    // In a real application this would be a webhook handler, Slack bot, or UI.
    await sessionClient.ResolveApprovalAsync(convId3, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Auto-approved by sample reviewer.",
    });

    Console.WriteLine(" [Reviewer] Approval submitted — waiting for assistant response...\n");
}
else
{
    Console.WriteLine(" [Main] Chat completed without triggering an approval gate.\n");
}

var r3 = await chatTask3;
Console.WriteLine($" Agent: {r3.Text}");
Console.WriteLine(" (delete_file ran after human approval — file is now gone from FakeFileSystem)");
Console.WriteLine("════════════════════════════════════════════════════════\n");

// ── Shutdown ──────────────────────────────────────────────────────────────────
// Signal Shutdown to each running workflow so it exits its main loop cleanly
// instead of sitting parked for SessionTimeToLive (10 minutes above).
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
