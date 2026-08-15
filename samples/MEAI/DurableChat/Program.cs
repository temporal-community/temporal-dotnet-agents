// DurableChat — demonstrates multi-turn durable chat via DurableChatSessionClient,
// including durable tool dispatch and history retrieval.
//
// Run:  dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Common;
using TemporalCommunity.Extensions.AI;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";
const string TaskQueue = "durable-chat";

// Configuration values may come from any of:
//   - appsettings.json (committed defaults — fine for OPENAI_API_BASE_URL, OPENAI_MODEL, TEMPORAL_ADDRESS)
//   - environment variables (always loaded by Host.CreateApplicationBuilder)
//   - user secrets (loaded only in Development environment; csproj declares <UserSecretsId>)
// OPENAI_API_KEY is sensitive — keep it in user secrets or env vars, never in appsettings.json.
if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException(
        "OPENAI_API_BASE_URL is not configured. Set it in appsettings.json, " +
        "as an environment variable, or via " +
        "`dotnet user-secrets set OPENAI_API_BASE_URL https://api.openai.com/v1 --project samples/MEAI/DurableChat`.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it as an environment variable or via " +
        "`dotnet user-secrets set OPENAI_API_KEY sk-... --project samples/MEAI/DurableChat`. " +
        "Note: user secrets only load in the Development environment (DOTNET_ENVIRONMENT unset or set to 'Development').");

// ── Setup: Tool functions ────────────────────────────────────────────────────
// Registering a function in a durable toolset below makes each call run as a
// separate Temporal activity — no special
// wrapping required on the tool itself.
//
// Tools run in activity context (not workflow), so `Random.Shared` is allowed.
// Do NOT use it inside `[Workflow]` code — replays would diverge.
static string GetCurrentWeather(string city)
    => Random.Shared.NextDouble() > 0.5
        ? $"It's sunny and 22 °C in {city}."
        : $"It's overcast and 15 °C in {city}.";

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_current_weather",
    description: "Returns the current weather conditions for a given city.");

var serviceStatusTool = AIFunctionFactory.Create(
    () => "All customer services are operational.",
    name: "get_service_status",
    description: "Returns the current customer-service status.");

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddChatClient registers the IChatClient as a singleton in DI. It returns a
// ChatClientBuilder for chaining middleware,
// but when no middleware is chained, the registration is already complete —
// `.Build()` would just return the same client and discard the value.
// DurableChatActivities constructor-injects this on the worker side.
//
// Durable sessions own the function-call loop. Do not add UseFunctionInvocation()
// to this pipeline; each registered tool is dispatched by the workflow as an activity.
IChatClient openAiChatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model).AsIChatClient();

builder.Services.AddChatClient(openAiChatClient);

// ── Setup: Connect Temporal client with DurableAIDataConverter ───────────────
// DurableAIDataConverter.Instance wraps the payload converter with
// AIJsonUtilities.DefaultOptions so MEAI's polymorphic AIContent subclasses
// ($type discriminators: TextContent, FunctionCallContent, etc.) survive
// round-trips through Temporal history.
//
// We connect the client manually and register it as ITemporalClient because
// DurableChatSessionClient is resolved from the root service provider after the
// host starts (see host.Services.GetRequiredService<DurableChatSessionClient>()
// below) and its factory depends on a root-resolvable ITemporalClient. The
// 3-arg AddHostedTemporalWorker(address, namespace, taskQueue) overload does NOT
// register a root-resolvable ITemporalClient, so resolving the session client
// from root would throw. Connecting explicitly here (and setting the data
// converter ourselves) is the pattern every MEAI sample that resolves
// DurableChatSessionClient from root uses.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// The 1-arg AddHostedTemporalWorker(taskQueue) overload binds the worker to the
// ITemporalClient registered above.
//
// AddDurableAI registers DurableChatWorkflow, DurableChatActivities, and
// DurableChatSessionClient on the worker. The session client is resolved from
// DI after the host starts.
//
// DefaultToolsetIds composes two named worker-owned toolsets in a stable order. The client never
// receives their schemas. The workflow resolves one versioned manifest, then dispatches each
// model-requested invocation as a separate InvokeFunction activity. Per-tool retry/timeout is
// frozen into that manifest through the DurableChatToolOptions callback.
// Default IDs and cross-selected-toolset visible-name collisions are validated while the worker
// starts, before a session can be created.
var durableWorker = builder.Services
    .AddHostedTemporalWorker(TaskQueue)
    .AddDurableAI(opts =>
    {
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
        opts.DefaultToolsetIds = ["information", "operations"];

        // Worker-level fallback for any tool that doesn't override its RetryPolicy.
        // Without this, RetryPolicy is null and Temporal applies its built-in
        // "retry forever" default — a footgun for transient failures in demos and
        // for write-style tools alike.
        opts.RetryPolicy = new RetryPolicy { MaximumAttempts = 3 };

        // Cap the LLM-to-tool loop per turn.
        opts.MaxToolCallsPerTurn = 10;
    });

// Per-tool retry-policy fallback chain:
//   per-tool opts.RetryPolicy (not set here) → worker `RetryPolicy = MaximumAttempts = 3`.
// Weather lookup is idempotent (read-only), so we accept retries. For write-style tools
// (send email, charge a card), override with `opts.NoRetry()`.
durableWorker.AddDurableToolset("information", tools => tools
    .Add(weatherTool, opts => opts.WithTimeout(TimeSpan.FromSeconds(30))));
durableWorker.AddDurableToolset("operations", tools => tools
    .Add(serviceStatusTool));

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// Track every conversation we start so we can signal Shutdown to each running
// workflow before the host exits (see "Shutdown" block below).
var conversationIds = new List<string>();

// ── Run demos ─────────────────────────────────────────────────────────────────
conversationIds.AddRange(await RunMultiTurnDemoAsync(sessionClient));
conversationIds.AddRange(await RunToolCallDemoAsync(sessionClient));
conversationIds.AddRange(await RunHistoryQueryDemoAsync(sessionClient));
conversationIds.AddRange(await DurableToolDemo.RunDurableToolDemoAsync(sessionClient));

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
// Demo 1: Multi-turn conversation
//
// Shows that conversation history is preserved across turns in the Temporal
// workflow. The second question ("that city") is only answerable because the
// workflow held onto the first turn's context.
// ═════════════════════════════════════════════════════════════════════════════
static async Task<IEnumerable<string>> RunMultiTurnDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 1: Multi-Turn Conversation");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Each conversation maps to a Temporal workflow. Reusing the same ID across
    // SendAsync calls routes all turns to the same workflow instance.
    var conversationId = $"multi-turn-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    var q1 = "What is the capital of France?";
    Console.WriteLine($" User : {q1}");
    // The durable activity applies these values to its current span. Temporal routing keys are
    // removed before the OpenAI provider receives the ChatOptions.
    var turnOptions = new ChatOptions()
        .WithChatClientTag("sample", "multi-turn")
        .WithChatClientTag("conversation_id", conversationId);
    var r1 = await sessionClient.SendAsync(
        conversationId,
        [new ChatMessage(ChatRole.User, q1)],
        turnOptions);
    Console.WriteLine($" Agent: {r1.Text}\n");

    // The workflow's history already contains the previous exchange, so the
    // model can answer this pronoun reference without being told explicitly.
    var q2 = "What is the population of that city?";
    Console.WriteLine($" User : {q2}");
    var r2 = await sessionClient.SendAsync(conversationId, [new ChatMessage(ChatRole.User, q2)]);
    Console.WriteLine($" Agent: {r2.Text}");

    Console.WriteLine("════════════════════════════════════════════════════════\n");
    return [conversationId];
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo 2: Tool call — registered durable tools
//
// Background: tool registration is split across two concerns.
//
//   - `AddDurableToolset(...)` (Program.cs above) registers each model declaration,
//     implementation, and durable policy in a named worker-owned group.
//
//   - The durable runtime supplies the tool SCHEMA to the model from that same
//     registry. Callers must not set ChatOptions.Tools for a durable session.
//
// Because the tool is registered in a durable toolset, the
// workflow dispatches it as a separate InvokeFunction activity instead of
// running it inline. One GetChatStep activity for the LLM call, one
// InvokeFunction activity for the tool call — visible side-by-side in the
// Temporal Web UI.
// ═════════════════════════════════════════════════════════════════════════════
static async Task<IEnumerable<string>> RunToolCallDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 2: Tool Call (registered durable tool)");
    Console.WriteLine("════════════════════════════════════════════════════════");

    var conversationId = $"tool-call-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    var q = "What is the weather like in Seattle right now?";
    Console.WriteLine($" User : {q}");

    // No ChatOptions.Tools: the workflow records the worker-owned manifest once,
    // then supplies its frozen declaration to each model activity.
    var response = await sessionClient.SendAsync(
        conversationId,
        [new ChatMessage(ChatRole.User, q)]);

    Console.WriteLine($" Agent: {response.Text}");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
    return [conversationId];
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo 3: History query
//
// Shows that the full conversation log is persisted in the Temporal workflow
// and can be retrieved at any time via GetHistoryAsync. This includes tool
// call and tool result messages, not just user/assistant text.
// ═════════════════════════════════════════════════════════════════════════════
static async Task<IEnumerable<string>> RunHistoryQueryDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 3: History Query");
    Console.WriteLine("════════════════════════════════════════════════════════");

    var conversationId = $"history-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    // Build up a short conversation to populate the history.
    await sessionClient.SendAsync(conversationId,
        [new ChatMessage(ChatRole.User, "Name three planets in our solar system.")]);

    await sessionClient.SendAsync(conversationId,
        [new ChatMessage(ChatRole.User, "Which of those is closest to the Sun?")]);

    // GetHistoryAsync sends a Temporal Query to the running workflow.
    // The workflow returns every DurableSessionEntry it has accumulated. Each turn
    // produces a request entry followed by a response entry; flatten the entries'
    // Messages to display individual ChatMessages.
    var history = await sessionClient.GetHistoryAsync(conversationId);
    var messages = history.SelectMany(e => e.Messages).ToList();

    Console.WriteLine(" Persisted history:");
    foreach (var msg in messages)
    {
        var role = msg.Role == ChatRole.User      ? "User "
                 : msg.Role == ChatRole.Assistant ? "Agent"
                 : msg.Role.Value;

        var text = string.Concat(msg.Contents.OfType<TextContent>().Select(c => c.Text));
        if (!string.IsNullOrWhiteSpace(text))
            Console.WriteLine($"   [{role}] {text}");
    }

    Console.WriteLine($"\n Total entries stored: {history.Count} ({messages.Count} messages)");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
    return [conversationId];
}
