// ApprovalScopes — demonstrates Feature B (Approval Scopes) with all three scope levels:
// ThisCallOnly, Session, and Always. Uses a JsonFileApprovalScopeStore so Always-scope
// decisions survive process restarts.
//
// Run:  dotnet run --project samples/MAF/ApprovalScopes/ApprovalScopes.csproj

using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using ApprovalScopes;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Agents.Approvals;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.AI.Approvals;

// OpenAI.Chat also defines ChatMessage and ChatRole; pin to the MEAI versions
// throughout this file so the conversation loop types remain unambiguous.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

// ── Configuration ────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ApprovalScopes");

var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Fake in-memory filesystem ─────────────────────────────────────────────────
// No real I/O — keeps the sample self-contained and portable.
var fakeFs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/tmp/readme.txt"]   = "Temporal ApprovalScopes sample — try writing files here.",
    ["/docs/overview.md"] = "Project overview (pre-existing).",
};

// ── Tool definitions ──────────────────────────────────────────────────────────
var listFilesTool = AIFunctionFactory.Create(
    ([Description("Directory path to list")] string directory) =>
    {
        var matches = fakeFs.Keys.Where(k => k.StartsWith(directory, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 0
            ? $"No files found under {directory}"
            : string.Join("\n", matches);
    },
    name: "list_files",
    description: "List files under a directory path.");

var writeFileTool = AIFunctionFactory.Create(
    ([Description("File path to write")] string path,
     [Description("Content to write")] string content) =>
    {
        fakeFs[path] = content;
        Console.WriteLine($"\n  [write_file] Wrote {content.Length} chars to {path}");
        return $"File written: {path} ({content.Length} chars)";
    },
    name: "write_file",
    description: "Write content to a file. WRITE — non-idempotent. Requires approval.");

// ── DI registration ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<JsonFileApprovalScopeStore>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker("approval-scopes-sample")
    .AddTemporalAgents(opts =>
    {
        // HITL with scope selection requires timeouts that cover the full human review
        // window. DefaultApprovalTimeout must be < DefaultActivityTimeout so the activity
        // outlives the approval window.
        opts.DefaultActivityTimeout  = TimeSpan.FromHours(1);
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(5);
        opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(55);

        opts.AddDurableAgent("FileAssistant", agent =>
        {
            agent.Instructions = """
                You are a file management assistant. Help users list and write files.
                Call write_file with the exact path and content the user specifies.
                Report success or failure clearly after each operation.
                """;
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            // list_files: read-only — skip the interceptor to avoid unnecessary overhead.
            agent.AddTool(listFilesTool, t => t.SkipInterceptor());

            // write_file: write tool — NoRetry() prevents double-execution on retry.
            // RequireApproval() is the Rule 2 absolute floor; ScopeAware() lets the
            // ScopedApprovalInterceptor bypass the gate when a matching scope is present.
            agent.AddTool(writeFileTool, t => t.NoRetry().RequireApproval().ScopeAware());

            // UseApprovalScopes installs the built-in ScopedApprovalInterceptor and
            // wires the JsonFileApprovalScopeStore for Always-scope persistence.
            agent.UseApprovalScopes(scopes =>
            {
                scopes.ApprovalScopeStore =
                    sp => sp.GetRequiredService<JsonFileApprovalScopeStore>();
            });
        });
    });

// ── Start the host ────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

var appLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var ct = appLifetime.ApplicationStopping;

// ── Banner ────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║         File Assistant — Approval Scopes Demo        ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║  Ask the assistant to list or write files.           ║");
Console.WriteLine("║  When write_file is called, choose an approval scope:║");
Console.WriteLine("║    [0] Deny                                          ║");
Console.WriteLine("║    [1] Approve (this call only)                      ║");
Console.WriteLine("║    [2] Approve for this session — any write_file     ║");
Console.WriteLine("║    [3] Approve for this session — /tmp/* (Glob)      ║");
Console.WriteLine("║    [4] Approve always — any write_file  [persisted]  ║");
Console.WriteLine("║    [5] Approve always — /tmp/* (Glob)  [persisted]   ║");
Console.WriteLine("║  Type 'quit' to exit.                                ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("Pre-existing files:");
foreach (var (path, _) in fakeFs)
    Console.WriteLine($"  {path}");
Console.WriteLine();

// ── Resolve services ──────────────────────────────────────────────────────────
var proxy  = host.Services.GetTemporalAgentProxy("FileAssistant");
var client = host.Services.GetRequiredService<ITemporalAgentClient>();

// ── Conversation session ──────────────────────────────────────────────────────
var session = await proxy.CreateSessionAsync();
if (session is not TemporalAgentSession temporalSession)
    throw new InvalidOperationException("Failed to retrieve TemporalAgentSession from the created session.");
var sessionId = temporalSession.SessionId;

// ── Main conversation loop ────────────────────────────────────────────────────
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    IList<ChatMessage> userMessages = [new(ChatRole.User, input)];

    // Start the agent call without awaiting — it may park inside write_file
    // waiting for an approval decision, so we need to stay responsive.
    var agentTask = proxy.RunAsync(userMessages, session, null, ct);

    Console.WriteLine("Assistant: (thinking...)");

    // Poll for pending approvals while the agent is running.
    // GetPendingApprovalAsync is a [WorkflowQuery] — never blocks the workflow.
    while (!agentTask.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        if (agentTask.IsCompleted) break;

        DurableApprovalRequest? pending = null;
        try
        {
            pending = await client.GetPendingApprovalAsync(sessionId, ct);
        }
        catch (Temporalio.Exceptions.RpcException ex)
            when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            // Workflow may not have started yet on the very first poll — retry.
            continue;
        }

        if (pending is null) continue;

        // ── Approval gate — scope selector ────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════╗");
        Console.WriteLine("  ║         APPROVAL REQUIRED                ║");
        Console.WriteLine("  ╚══════════════════════════════════════════╝");
        if (pending.Description is { } desc)
            Console.WriteLine($"  {desc}");
        Console.WriteLine();
        Console.WriteLine("  Scope options:");
        Console.WriteLine("    [0] Deny");
        Console.WriteLine("    [1] Approve (this call only)");
        Console.WriteLine("    [2] Approve for this session  — any write_file call");
        Console.WriteLine("    [3] Approve for this session  — paths matching /tmp/* (Glob)");
        Console.WriteLine("    [4] Approve always            — any write_file call  [persisted]");
        Console.WriteLine("    [5] Approve always            — paths matching /tmp/* (Glob) [persisted]");

        string choice;
        do
        {
            Console.Write("\n  Enter choice [0-5]: ");
            choice = (Console.ReadLine() ?? string.Empty).Trim();
        }
        while (choice is not "0" and not "1" and not "2" and not "3" and not "4" and not "5");

        if (choice == "0")
        {
            await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                Approved  = false,
                Reason    = "Denied by user.",
            });
            Console.WriteLine("\n  Denied.");
            continue;
        }

        // Build the optional argument-level scope pattern.
        // Choices 3 and 5 scope to paths matching /tmp/* via Glob on the "path" parameter.
        ApprovalScopePattern? pattern = choice is "3" or "5"
            ? new ApprovalScopePattern
              {
                  Type      = PatternMatchType.Glob,
                  Parameter = "path",
                  Pattern   = "/tmp/*",
              }
            : null;

        ApprovalScope scope = choice switch
        {
            "2" or "3" => ApprovalScope.Session,
            "4" or "5" => ApprovalScope.Always,
            _           => ApprovalScope.ThisCallOnly,
        };

        // SubmitApprovalAsync is a [WorkflowUpdate] — strongly consistent,
        // validates the RequestId, and unblocks WaitConditionAsync in the workflow.
        await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
        {
            RequestId    = pending.RequestId,
            Approved     = true,
            Scope        = scope,
            ScopePattern = pattern,
        });

        var label = (scope, pattern) switch
        {
            (ApprovalScope.Session, null)     => "session (any write_file)",
            (ApprovalScope.Session, not null) => "session (paths matching /tmp/*)",
            (ApprovalScope.Always,  null)     => "always (any write_file) — persisted",
            (ApprovalScope.Always,  not null) => "always (paths matching /tmp/*) — persisted",
            (ApprovalScope.ThisCallOnly, _)   => "this call only",
            _                                 => throw new UnreachableException("Unexpected scope/pattern combination."),
        };
        Console.WriteLine($"\n  Approved [{label}] — agent is resuming...\n");
        // No break: the agent may call write_file again in the same turn, queuing
        // another approval. The poll loop continues until agentTask completes.
    }

    AgentResponse response;
    try
    {
        response = await agentTask;
    }
    catch (OperationCanceledException)
    {
        break;
    }

    Console.WriteLine($"Assistant: {response.Text}");
    Console.WriteLine();
}

try { await client.ShutdownAsync(sessionId); } catch (OperationCanceledException) { }
try { await host.StopAsync(); } catch (OperationCanceledException) { }
