# MAF Quick Start and Usage Guide

Start with the [Library Combinations Guide](../../library-combinations.md) to select the package,
then choose a runnable project from the [Sample Catalog](../../../samples/catalog.md). This page
starts with the worker-hosted path and links to the focused guides for advanced behavior.

For externally reachable session or approval endpoints, apply the normative
[security boundary](../../security.md) before calling a durable client.

---

## Quick Start

`AddDurableAgent` is the only **worker-hosted durable-agent definition** path. The client-only
counterpart is `AddTemporalAgentProxies` plus `AddAgentProxy`; it declares proxies for an agent
hosted by another process and does not register a worker or agent implementation.

`AddDurableAgent(string name, Action<DurableAgentBuilder> configure)` consolidates the worker's chat
client, instructions, durable tools, compatible context providers, and per-agent timeouts. It does
not make provider-owned external history durable. DI access is provided via per-slot factories on
the builder, so you do not need to call `BuildServiceProvider()` to wire dependencies.

### Worker-hosted example

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Description = "Issues refunds and notifies the customer.";
            agent.Instructions = "You are a refund specialist.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools must opt out of retry — non-idempotent re-execution is the foot-gun.
            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    "apply_refund"),
                opts => opts.NoRetry());

            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    "send_email"),
                opts => opts.NoRetry());

            agent.MaxToolCallsPerTurn = 10;
        });
    })
    .AddWorkflow<RefundWorkflow>();
```

> **`ITemporalClient` prerequisite:** `AddTemporalAgents` requires `ITemporalClient` to be registered in DI. While the three-argument `AddHostedTemporalWorker(address, namespace, queue)` overload configures a worker-internal client, it does not register `ITemporalClient` in DI. Always call `builder.Services.AddTemporalClient(address, namespace)` before `AddHostedTemporalWorker`.

### Continue from here

| Need | Canonical guide |
| --- | --- |
| Select a package or sample | [Library Combinations Guide](../../library-combinations.md) and [Sample Catalog](../../../samples/catalog.md) |
| Use a client-only process | [Invoking Agents from External Code](#invoking-agents-from-external-code-proxy) |
| Configure tools, retries, and writes | [Durable Agents](durable-agents.md) |
| Add a compatible context provider | [Individual MAF Context Providers](individual-context-providers.md) |
| Understand supported MAF inputs | [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md) |
| Manage prompt/context size | [History & Token Optimization](prompt-caching.md) |

### `DurableAgentBuilder` reference

| Property / Method | Purpose |
|-------------------|---------|
| `Name` (read-only) | Case-insensitive agent name passed in to `AddDurableAgent`. |
| `Description` | Used in `GetAgentDescriptors()` for routing prompts. Optional. |
| `Instructions` | Agent system prompt. Library stamps onto every LLM call's `ChatOptions.Instructions`. Optional. |
| `ChatClient` | **Required.** `Func<IServiceProvider, IChatClient>` factory invoked at activity execution time to resolve the model's `IChatClient`. Throws `InvalidOperationException` if omitted. |
| `ChatOptions` | LLM-call template (Temperature, ResponseFormat, MaxOutputTokens, etc.). `Tools` and `Instructions` set on this property are ignored. |
| `AddTool(AIFunction tool, Action<DurableToolOptions>? configure = null)` | Registers a concrete `AIFunction`. Per-tool retry / timeout via `configure`. |
| `AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure = null)` | DI-resolving tool factory. |
| `AddTools(params AIFunction[] tools)` | Bulk registration of concrete tools. |
| `AddContextProvider(AIContextProvider provider, IEnumerable<DurableToolRegistrationSpec>? durableTools = null)` / `AddContextProvider(Func<IServiceProvider, AIContextProvider>)` | Wires a provider into the chat pipeline. `Invoking/InvokedAsync` fire once per LLM call. Concrete providers can also contribute durable tools through specs or `IDurableToolSource`. |
| `TimeToLive`, `ApprovalTimeout`, `ActivityTimeout`, `HeartbeatTimeout` | Per-agent overrides. `null` inherits the worker-level default on `TemporalAgentsOptions`. |
| `RetryPolicy` | Retry policy for the agent's `RunAgentStep` activity (the LLM call). Per-tool retry is configured separately via `DurableToolOptions`. |
| `MaxEntryCount`, `HistoryReducerKey` | Per-agent continue-as-new bounds and keyed reducer. Inherit worker defaults when unset. |
| `MaxToolCallsPerTurn` | Cap on LLM-step iterations per agent turn (default `20` when not set). Applies across all three execution paths: session-based workflows, scheduled jobs, and sub-agent orchestration via `GetTemporalAgent()`. No worker-level fallback. **Resolution timing:** The value is resolved from the agent registration on the first LLM step of the first turn and cached for the lifetime of the `TemporalAIAgent` session instance. Changes to the builder value after worker startup do not affect sessions already in progress. |
| `AddToolInterceptor(Func<IServiceProvider, IAgentToolInterceptor> factory)` | Registers a pre-tool lifecycle hook. The interceptor runs before each `InvokeAgentTool` activity and returns `DurableToolDecision` (from `TemporalCommunity.Extensions.AI`): `Proceed`, `PauseForApproval`, `Skip`, or `Block`. See `opts.DefaultToolInterceptor` for a worker-level default. |

### `DurableToolOptions` reference

| Property / Method | Purpose |
|-------------------|---------|
| `StartToCloseTimeout`, `HeartbeatTimeout`, `RetryPolicy` | Standard Temporal activity overrides. `null` inherits worker default. |
| `NoRetry()` | Sets `RetryPolicy = new() { MaximumAttempts = 1 }`. Use on write tools. |
| `WithMaxAttempts(int n)` | Sets a fixed-retry policy. |
| `WithTimeout(TimeSpan t)` | Sets `StartToCloseTimeout`. |
| `SkipInterceptor()` | Bypasses `IAgentToolInterceptor` for this specific tool. |
| `WithInterceptorTimeout(TimeSpan t)` | Per-tool timeout for the interceptor activity. |
| `RequireApproval()` | Absolute floor: always pause for human approval even if the interceptor returns `Proceed`. |

### Inheritance — per-agent vs worker-level

For every scalar setting the rule is: **if you set it on the agent, it overrides the worker default; if you leave it `null`, the worker-level default applies.**

| Per-agent setting (`DurableAgentBuilder`) | Worker default (`TemporalAgentsOptions`) |
|-------------------------------------------|------------------------------------------|
| `agent.TimeToLive` | `opts.DefaultTimeToLive` |
| `agent.ApprovalTimeout` | `opts.DefaultApprovalTimeout` |
| `agent.ActivityTimeout` | `opts.DefaultActivityTimeout` |
| `agent.HeartbeatTimeout` | `opts.DefaultHeartbeatTimeout` |
| `agent.RetryPolicy` | `opts.DefaultRetryPolicy` |
| `agent.MaxEntryCount` | `opts.DefaultMaxEntryCount` |
| `agent.HistoryReducerKey` | `opts.DefaultHistoryReducerKey` |
| `agent.MaxToolCallsPerTurn` | *no worker fallback — defaults to `20`; propagates to scheduled jobs and sub-agent orchestration* |
| `agent.AddToolInterceptor(...)` | `opts.DefaultToolInterceptor` — worker-level fallback; overridden per agent via `AddToolInterceptor` |

The retry-policy hierarchy adds one more layer specifically for tools. From most to least specific:

1. `agent.AddTool(t, opts => opts.DefaultRetryPolicy = ...)` — the per-tool override (use `opts.NoRetry()` on write tools).
2. `agent.RetryPolicy` — the agent-level default for any tool that doesn't override.
3. `opts.DefaultRetryPolicy` — the worker-level default used by agents that don't override.

There is **no per-agent "default for all my tools" cascade beyond `agent.RetryPolicy`** — set policies per tool when the per-tool default is genuinely different.

### Lifecycle and composition

Tool factories run once when the worker first builds its immutable agent blueprint and their `AIFunction` values are cached for that worker. The chat-client, context-provider, and interceptor factories run from a fresh DI scope for every activity attempt. Do not use provider fields as session storage: an attempt can retry, run on another worker, or overlap another session; use `AgentSession.StateBag` instead.

The library composes the chat pipeline internally and passes `UseProvidedChatClientAsIs = true` to MAF so that `FunctionInvokingChatClient` is **not** auto-injected — the workflow owns the tool-dispatch loop. Register a bare `IChatClient` in DI (do not call `.UseFunctionInvocation()`).

Custom agent middleware belongs in `ConfigureAgentPipeline`. Its wrapper must derive from
`DelegatingAIAgent`, preserve the exact supplied `inner` agent through `base(inner)`, and delegate
both run shapes it customizes:

```csharp
sealed class TimingAgent(AIAgent inner, ILogger<TimingAgent> logger)
    : DelegatingAIAgent(inner)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await base.RunCoreAsync(messages, session, options, cancellationToken);
        }
        finally
        {
            logger.LogInformation("Agent run completed in {Elapsed}",
                Stopwatch.GetElapsedTime(started));
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var update in base.RunCoreStreamingAsync(
                messages, session, options, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            logger.LogInformation("Agent stream completed in {Elapsed}",
                Stopwatch.GetElapsedTime(started));
        }
    }
}

agent.ConfigureAgentPipeline = pipeline =>
    pipeline.Use((inner, services) =>
        new TimingAgent(inner, services.GetRequiredService<ILogger<TimingAgent>>()));
```

Do not return a separate agent from the factory, and do not hide `inner` inside a custom
`AIAgent` subclass. Both shapes are rejected because the library cannot prove that its
`ChatClientAgent` remains the durable model-call leaf. Request-level short-circuiting inside a
valid `DelegatingAIAgent` remains supported.

The pipeline callback runs once during worker-startup validation and once for each
`RunDurableAgentStep` activity attempt, including retries. Both validation and live construction
use a DI scope, so middleware factories may resolve scoped dependencies. Treat wrapper fields as
attempt-local, never session-local.

Custom middleware wrappers must not implement `IDisposable` or `IAsyncDisposable`: MAF 1.17.0 does
not expose whether a factory-created wrapper or DI owns that instance, so the library rejects that
ambiguous shape. Put resource-owning dependencies in the activity DI scope instead. The one
supported exception is MAF's built-in `OpenTelemetryAgent`; the library knows that wrapper owns
its internal telemetry client and disposes it at the end of each validation or activity build.
If `AIAgentBuilder.Build` throws before returning a root, MAF does not expose any partially built
wrappers, so this library cannot dispose inaccessible instances.

Live middleware receives the restored `TemporalAgentSession`. It may persist retry-safe state in
the supplied session's `StateBag`, but it must pass the exact session object to `next`:

```csharp
sealed class AttemptCountingAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var durable = (TemporalAgentSession)session!;
        // Read/write a JSON-serializable StateBag value here. The activity persists the bag.

        await foreach (var update in base.RunCoreStreamingAsync(
            messages, durable, options, cancellationToken))
        {
            yield return update;
        }
    }
}
```

Do not pass `null` or substitute another session. The library rejects either shape because only the
original restored StateBag is durably serialized. The final `ChatClientAgent` uses a separate,
transient `ChatClientAgentSession` behind the library's innermost boundary; middleware that
requires that leaf-specific type is not supported.

`AIContextProvider.InvokingAsync` and `InvokedAsync` fire **once per LLM call** (per `RunDurableAgentStep` activity). A turn that takes 3 LLM-step iterations to converge will see 3 invocation pairs. Make these hooks idempotent and cheap, or cache results via `StateBag` to skip redundant work within a turn.

For the workflow-loop semantics (per-tool fan-out, crash safety, continue-as-new) see [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../architecture/MAF/agent-sessions-and-workflow-loop.md).
For the supported MAF agent/provider boundary, see [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md).

---

## Library Dependencies

`TemporalCommunity.Extensions.Agents` depends on `TemporalCommunity.Extensions.AI`. Installing the Agents NuGet package pulls in the AI package automatically — no separate `<PackageReference>` for `TemporalCommunity.Extensions.AI` is needed.

The shared HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) are defined in `TemporalCommunity.Extensions.AI`. Ordinary decisions apply to one call. MAF reusable session grants use the separately registered `ITemporalAgentApprovalScopeAdministration` capability.

---

## Table of Contents

1. [Sending Messages](#sending-messages)
2. [Multi-Turn Conversations](#multi-turn-conversations)
3. [Reducing the LLM Context Window](#reducing-the-llm-context-window)
4. [Fire-and-Forget](#fire-and-forget)
5. [Structured Output](#structured-output)
6. [Tool Filtering](#tool-filtering)
7. [Agent Orchestration (Inside Workflows)](#agent-orchestration-inside-workflows)
8. [Invoking Agents from External Code (Proxy)](#invoking-agents-from-external-code-proxy)
9. [Session Identity](#session-identity)
10. [Session TTL](#session-ttl)
11. [Activity Timeouts](#activity-timeouts)
12. [Accessing Temporal from Agent Tools](#accessing-temporal-from-agent-tools)
13. [Streaming Limitation](#streaming-limitation)
14. [Routing](#routing)
15. [Parallel Agent Execution](#parallel-agent-execution)
16. [Human-in-the-Loop (HITL) Approval Gates](#human-in-the-loop-hitl-approval-gates)
17. [Scheduling](#scheduling)
18. [MCP Tool Integration](#mcp-tool-integration)
19. [Context Providers and External Memory](#context-providers-and-external-memory)
20. [Per-Tool Activity Configuration](#per-tool-activity-configuration)
21. [OpenTelemetry Integration](#opentelemetry-integration)

---

## Sending Messages

### External Caller

```csharp
// Create (or resume) a session
AgentSession session = await agentProxy.CreateSessionAsync();

// Send a message and get a response
AgentResponse response = await agentProxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Messages[0].Text);
```

For a new session, the built-in client delivers the first call with Temporal Update-With-Start. The
workflow waits deterministically for its typed input before processing that Update. Fire-and-forget
and delayed starts use the equivalent Signal-With-Start path and share the same readiness guarantee;
callers do not need a separate start or retry step.

If you author a custom workflow, keep Update validators synchronous. Validators cannot await workflow
initialization; put checks that depend on initialized workflow input in the Update handler after a
deterministic readiness wait.

The session ID encodes the agent name and a unique key as a Temporal workflow ID (`ta-myagent-{key}`). Passing the same
session across calls routes all messages to the same `AgentWorkflow` instance, preserving conversation history.

### Quick One-Shot Call

For simple one-off requests where you don't need to manage sessions, create an explicit session with a well-known key or use a randomly-keyed session:

```csharp
ITemporalAgentClient client = // resolved from DI

// Explicit session — recommended pattern
var session = new TemporalAgentSessionId("MyAgent", Guid.NewGuid().ToString("N"));
AgentResponse response = await client.SendAsync(session, new RunRequest("What is the capital of France?"));
```

> **Note:** The `RunAgentAsync(string agentName, string message)` string convenience overload is deprecated (`[Obsolete]`). Prefer constructing a `TemporalAgentSessionId` and calling `SendAsync(sessionId, request)` directly so you retain a handle to the session for follow-up turns.

---

## Multi-Turn Conversations

```csharp
var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("What is the capital of France?", session);
Console.WriteLine(r1.Messages[0].Text);  // Paris

var r2 = await agentProxy.RunAsync("What is its population?", session);
Console.WriteLine(r2.Messages[0].Text);  // ~2.1 million (context preserved)
```

---

## Reducing the LLM Context Window

For long-running agent sessions the conversation history accumulated in the
`AgentWorkflow` can grow large enough to make each LLM call expensive.
`TemporalCommunity.Extensions.Agents` works with the same MEAI `IChatReducer` family
as `TemporalCommunity.Extensions.AI`: register a stateless reducer such as
`MessageCountingChatReducer` on the underlying `IChatClient` that backs your
`AIAgent`. The reducer applies a sliding window at the LLM-call boundary —
inside `AgentActivities.RunDurableAgentStepAsync` — so it does not need to be replay-safe.

```csharp
var chatClient = openAiClient.GetChatClient("gpt-4o-mini")
    .AsBuilder()
    .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
    .Build();
//  Note: do NOT call .UseFunctionInvocation() — the durable-agent path composes
//  the chat pipeline internally and tools are dispatched as separate Temporal activities.

builder.Services.AddChatClient(chatClient);
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

With this configuration:

- The `AgentWorkflow`'s `_history` retains every message ever exchanged in the
  session — that is the durable, replay-safe source of truth and survives worker
  restarts and continue-as-new transitions.
- The reducer passes only the most recent 20 messages to the LLM on each turn.
- Querying `_history` (e.g., for audit) still returns the full unreduced log.

> **Design rationale:** Conversation history lives on the workflow itself, where
> it is replay-safe via Temporal event history. Reducers shape only what is sent
> to the LLM per turn — they never own conversation state.

> **Note:** `MessageCountingChatReducer` is provided by the MEAI library
> (`Microsoft.Extensions.AI`). Any `IChatReducer` implementation works —
> token-counting reducers, summarization reducers, etc. — as long as it is
> stateless or scoped per call. This is prompt shaping only: an `IChatReducer`
> is not a durable `DurableSessionEntry` compaction adapter. Use the keyed
> `HistoryReducerKey` path for continue-as-new history reduction.

See the equivalent guidance for `TemporalCommunity.Extensions.AI` in
[the MEAI usage guide](../MEAI/usage.md#reducing-the-llm-context-window).

---

## Fire-and-Forget

For notifications or background tasks where you don't need to wait for the agent's response:

```csharp
var options = new TemporalAgentRunOptions { IsFireAndForget = true };
await agentProxy.RunAsync("Process this in the background.", session, options);
// Returns immediately with an empty AgentResponse
```

---

## Structured Output

### Using `RunAsync<T>` (Recommended)

`StructuredOutputExtensions.RunAsync<T>` deserializes the agent's text response directly into a typed object. It
automatically strips markdown code fences (`` ```json ... ``` ``) that many models wrap around JSON output, and retries
with error context when deserialization fails — allowing the LLM to self-correct:

```csharp
var session = await agentProxy.CreateSessionAsync();

// Automatically strips code fences, deserializes, and retries on failure
WeatherReport report = await agentProxy.RunAsync<WeatherReport>(
    new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") },
    session);
```

Control retry behavior with `StructuredOutputOptions`:

```csharp
var report = await agentProxy.RunAsync<WeatherReport>(
    new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") },
    session,
    new StructuredOutputOptions
    {
        MaxRetries = 3,                // default: 2
        IncludeErrorContext = true,     // default: true — appends error details to retry prompt
        JsonSerializerOptions = myOpts  // default: null — uses JsonSerializerOptions.Default
    });
```

`RunAsync<T>` is also available on `TemporalAIAgent` (inside workflows) and `ITemporalAgentClient`:

```csharp
// Inside a workflow
var agent = TemporalWorkflowExtensions.GetTemporalAgent("AnalystAgent");
var session = await agent.CreateSessionAsync();
var analysis = await agent.RunAsync<AnalysisResult>(messages, session);

// Via the client
var result = await client.RunAgentAsync<WeatherReport>(sessionId, request);
```

### Using `ChatResponseFormat` (Format Hint Only)

To hint the response format without automatic deserialization:

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var session = await agentProxy.CreateSessionAsync();
var response = await agentProxy.RunAsync("What's the weather in Seattle?", session, options);
var report = response.Messages[0].GetContent<WeatherReport>();
```

---

## Tool Filtering

Restrict which tools the agent may use for a specific request:

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather", "search_web"],
    // EnableToolCalls = false  // disable all tools for this request
};

var response = await agentProxy.RunAsync("Look up the latest news.", session, options);
```

The selection contract is exact:

| `EnableToolCalls` | `EnableToolNames` | Tools exposed and dispatchable |
|---|---|---|
| `false` | any value | none |
| `true` | `null` | all registered tools |
| `true` | empty | none |
| `true` | names | case-insensitive registered matches only |

The workflow checks the same policy again when the model returns a function call. Unknown,
blank, and excluded names receive a generic blocked result; they do not schedule an interceptor,
approval, or tool activity. Mixed batches retain model-call order while allowed calls continue
through normal durable dispatch.

Tool selection is exposure control, not authorization. It reduces what the provider sees and
what the workflow will dispatch for that run, but it does not establish the caller's identity or
permission to perform an effect. Side-effecting tools must re-authorize against current,
authoritative application data inside the tool activity. Tenant-visible blocked responses do not
distinguish an unknown tool from a registered-but-excluded tool; requested names are available only
in operator workflow logs, without arguments or registry contents.

---

## Agent Orchestration (Inside Workflows)

Use `TemporalWorkflowExtensions.GetTemporalAgent` to interact with agents from within an orchestrating Temporal workflow. The
agent's conversation history is stored in the workflow's event history and replayed automatically.

```csharp
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        // Get a TemporalAIAgent — runs inference via activity, history tracked in workflow state
        var researcher = TemporalWorkflowExtensions.GetTemporalAgent("ResearcherAgent");
        var session = await researcher.CreateSessionAsync();

        var outline = await researcher.RunAsync($"Create an outline about: {topic}", session);

        var writer = TemporalWorkflowExtensions.GetTemporalAgent("WriterAgent");
        var writerSession = await writer.CreateSessionAsync();

        var draft = await writer.RunAsync(
            $"Write a short article based on this outline:\n{outline.Messages[0].Text}",
            writerSession);

        return draft.Messages[0].Text;
    }
}
```

`TemporalAIAgent` (returned by `GetTemporalAgent`) stores the conversation history as workflow state. This means it survives
worker restarts, supports retries, and is durable by design — all without any extra persistence code.

---

## Invoking Agents from External Code (Proxy)

Use `TemporalAIAgentProxy` to interact with a registered agent from outside a Temporal workflow — for example, from an
ASP.NET handler, a background service, or a console application. The proxy communicates with the running `AgentWorkflow`
via Temporal workflow updates and is the correct counterpart to `TemporalWorkflowExtensions.GetTemporalAgent`, which is
workflow-context only.

`TemporalAIAgentProxy` is `internal`; callers always reference it as `AIAgent` (MAF's base class). Resolution is always
via `services.GetTemporalAgentProxy("Name")`.

| | `TemporalAIAgent` | `TemporalAIAgentProxy` |
|---|---|---|
| Returned by | `TemporalWorkflowExtensions.GetTemporalAgent("Name")` | `services.GetTemporalAgentProxy("Name")` |
| Context | Inside a `[Workflow]` method | Outside a workflow (ASP.NET, console, background service) |
| History | Stored in the calling workflow's event history | Stored in the target `AgentWorkflow`'s event history |
| Session | New or existing `TemporalAgentSession` | Same |

> **Misuse guard:** `TemporalWorkflowExtensions.GetTemporalAgent` throws `InvalidOperationException` when called outside a
> workflow context with the message: _"If you need to invoke an agent from external code, resolve a
> TemporalAIAgentProxy from your service provider via GetTemporalAgentProxy(name) instead."_
> Additionally, `TemporalAIAgent.RunAsync` (via `RunCoreAsync`) throws `InvalidOperationException` if invoked
> outside a Temporal workflow context — for instance, if a `TemporalAIAgent` reference escapes the workflow
> executor onto a non-workflow thread. In both cases the fix is the same: use `GetTemporalAgentProxy` for
> external callers.

### Same-Process Registration

When the worker and the caller live in the same process, calling `AddTemporalAgents(...)` on the worker builder
automatically registers a keyed `AIAgent` proxy singleton for every declared agent. No additional setup is required —
call `GetTemporalAgentProxy` directly against the host's service provider:

```csharp
// Worker and caller in the same process
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.Instructions = "You help customers with support requests.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    });

// ...

// Resolve and use the proxy — return type is AIAgent
AIAgent agentProxy = host.Services.GetTemporalAgentProxy("SupportAgent");

var session = await agentProxy.CreateSessionAsync();
AgentResponse response = await agentProxy.RunAsync("My order hasn't arrived.", session);

Console.WriteLine(response.Messages[0].Text);
```

### Split-Process Registration

When the Temporal worker runs in a separate process (for example, a dedicated worker binary next to an API server),
use `AddTemporalAgentProxies` on the client process's `IServiceCollection`. No worker is registered — only the client
infrastructure and the declared proxy singletons.

```csharp
// Client-only process (e.g. ASP.NET API server)
builder.Services.AddTemporalAgentProxies(
    configure: opts =>
    {
        opts.AddAgentProxy("SupportAgent");
        opts.AddAgentProxy("BillingAgent");
    },
    taskQueue: "agents",
    targetHost: "localhost:7233");

// ...

// ASP.NET controller or minimal API handler
app.MapPost("/chat", async (string message, IServiceProvider services) =>
{
    AIAgent agentProxy = services.GetTemporalAgentProxy("SupportAgent");

    var session = await agentProxy.CreateSessionAsync();
    AgentResponse response = await agentProxy.RunAsync(message, session);

    return Results.Ok(response.Messages[0].Text);
});
```

`AddTemporalAgentProxies` registers `ITemporalAgentClient` and the keyed `AIAgent` proxies only. When `targetHost` is
provided it also registers an `ITemporalClient`. If the client is already registered elsewhere (e.g., via
`AddTemporalClient`), omit `targetHost` and the existing registration is used.

### Multi-Turn Conversations via Proxy

The proxy's `CreateSessionAsync` and `RunAsync` signatures are identical to those of `TemporalAIAgent`. Retain the
session across calls to preserve conversation history:

```csharp
AIAgent agentProxy = services.GetTemporalAgentProxy("SupportAgent");

var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("My order hasn't arrived.", session);
Console.WriteLine(r1.Messages[0].Text);

var r2 = await agentProxy.RunAsync("The order number is 12345.", session);
Console.WriteLine(r2.Messages[0].Text);  // Context from r1 is preserved
```

For the in-workflow counterpart, see [Agent Orchestration (Inside Workflows)](#agent-orchestration-inside-workflows).

---

## Session Identity

A `TemporalAgentSessionId` directly maps to a Temporal workflow ID:

```
ta-{agentName (lowercase)}-{key}
```

You can create sessions with explicit keys for deterministic session routing (e.g., one session per user ID):

```csharp
// Deterministic: always routes to the same workflow for a given userId
var sessionId = new TemporalAgentSessionId("MyAgent", userId);
var session = new TemporalAgentSession(sessionId);

var response = await agentProxy.RunAsync("Hello!", session);
```

A session is owned by the agent name encoded in its workflow ID. Pass it only to the proxy that
created it (or to a proxy for the same agent name); another agent proxy rejects it before any
Temporal request is sent.

---

## Session TTL

Sessions expire after the configured TTL (default: 14 days). Configure per-agent overrides on the builder:

```csharp
opts.AddDurableAgent("ShortLivedAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.TimeToLive = TimeSpan.FromHours(1);
});

// Or configure the default for all agents on this worker
opts.DefaultTimeToLive = TimeSpan.FromDays(7);
```

When the TTL elapses, the `AgentWorkflow` completes naturally. The next message to that session ID starts a fresh
workflow run.

---

## Activity Timeouts

Every agent turn — one call to `RunAsync` — executes inside a Temporal activity. Two timeouts govern that activity:

| Option              | Default   | What it limits                                                                                           |
|---------------------|-----------|----------------------------------------------------------------------------------------------------------|
| `ActivityTimeout`   | 5 minutes | Total wall-clock time for one turn, including tool calls and retries                                     |
| `HeartbeatTimeout`  | 2 minutes | Maximum gap between heartbeats emitted while the model-step activity consumes provider updates |

Both are non-nullable `TimeSpan` properties on `TemporalAgentsOptions` with the defaults shown above. Override either
to tune for slow models or long tool-call chains.

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        // Increase for slow models or long tool-call chains
        opts.DefaultActivityTimeout = TimeSpan.FromMinutes(10);

        // Increase for long-running model calls
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(2);

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });
    });
```

### Activity Timeouts for In-Workflow Agents

When using `TemporalWorkflowExtensions.GetTemporalAgent` inside an orchestrating workflow, pass `ActivityOptions` directly at
the call site:

```csharp
var researcher = TemporalWorkflowExtensions.GetTemporalAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

---

## Accessing Temporal from Agent Tools

Agent tools executing inside `AgentActivities.InvokeAgentToolAsync` can access Temporal capabilities through
`TemporalAgentContext.Current`:

```csharp
public class MyAgentTool
{
    [Description("Start a background processing job")]
    public static async Task<string> StartJobAsync(string payload)
    {
        var context = TemporalAgentContext.Current;

        // Start a Temporal workflow from within an agent tool
        var workflowId = await context.StartWorkflowAsync(
            (ProcessingWorkflow wf) => wf.RunAsync(payload),
            new WorkflowOptions("job-" + Guid.NewGuid(), taskQueue: "jobs"));

        return $"Job started with ID: {workflowId}";
    }
}
```

`TemporalAgentContext` also exposes the current session:

```csharp
var sessionId = context.CurrentSession.SessionId;
Console.WriteLine($"Processing request for session: {sessionId.WorkflowId}");
```

---

## Streaming Limitation

`TemporalAIAgent` and `TemporalAIAgentProxy` do not support `RunStreamingAsync`; both throw
`NotSupportedException`. The durable model-step activity may consume provider updates internally
to form one durable response, but it never exposes an at-least-once token stream to callers.
Use `RunAsync` and deliver the completed response from your application boundary.

---

## Routing

Routing belongs inside your workflow, where every decision is durable, visible in history, and replayed from cache on crash recovery. The library provides two patterns:

- **Static routing** — a classifier agent runs inside the workflow and the result drives a switch statement with hardcoded agent names. Best for a fixed agent set.
- **Dynamic routing via activity** — the workflow discovers available agents by querying the descriptor registry inside an activity (whose result is cached in history). Best when the set of agents changes across deployments.

Both patterns are covered in detail in [Routing Patterns](./routing.md), with complete working code in `samples/MAF/WorkflowRouting/`.

---

## Parallel Agent Execution

`TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` dispatches multiple agent calls concurrently inside a workflow
using `Workflow.WhenAllAsync` — the workflow-safe equivalent of `Task.WhenAll`.

```csharp
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents;

[Workflow]
public class ResearchAndSummarizeWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var researchAgent  = TemporalWorkflowExtensions.GetTemporalAgent("ResearchAgent");
        var summaryAgent   = TemporalWorkflowExtensions.GetTemporalAgent("SummaryAgent");

        var researchSession = TemporalWorkflowExtensions.NewAgentSessionId("ResearchAgent");
        var summarySession  = TemporalWorkflowExtensions.NewAgentSessionId("SummaryAgent");

        var researchMessages = new List<ChatMessage>
            { new(ChatRole.User, $"Research the topic: {topic}") };
        var summaryMessages  = new List<ChatMessage>
            { new(ChatRole.User, $"Summarize the latest findings on: {topic}") };

        IReadOnlyList<AgentResponse> results =
            await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
            {
                (researchAgent, (IList<ChatMessage>)researchMessages, (AgentSession)new TemporalAgentSession(researchSession)),
                (summaryAgent,  (IList<ChatMessage>)summaryMessages,  (AgentSession)new TemporalAgentSession(summarySession)),
            });

        return $"Research: {results[0].Messages[0].Text}\n\nSummary: {results[1].Messages[0].Text}";
    }
}
```

Results are returned in the same order as the input tuples. Each agent runs inside its own activity and the workflow
waits for all of them before continuing.

---

## Human-in-the-Loop (HITL) Approval Gates

Agent tools can pause mid-turn and wait for a human decision before proceeding. The backing `AgentWorkflow` exposes
the inherited `ResolveApprovalAsync` update, plus `RequestApprovalAsync` (called from inside a tool) and one
`[WorkflowQuery]` handler, `GetPendingApproval`, for polling the current pending
request without modifying workflow state.

### Requesting Approval (Inside a Tool)

Call `TemporalAgentContext.Current.RequestApprovalAsync` from inside a tool implementation. The call blocks the activity
until a human submits a decision:

```csharp
public class DataDeletionTool
{
    [Description("Deletes all records for the specified user")]
    public static async Task<string> DeleteUserDataAsync(string userId)
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Delete all data for user — userId={userId}. This action is irreversible."
            });

        if (!decision.Approved)
        {
            return $"Action rejected by reviewer: {decision.Reason}";
        }

        // Proceed with deletion...
        return $"Data for user {userId} has been deleted.";
    }
}
```

Because the tool runs inside a Temporal activity, the pause is fully durable. If the worker restarts while waiting for
approval, the activity resumes from exactly the same point once a new worker picks it up.

Set `ActivityTimeout` to a value that exceeds your expected review time:

```csharp
opts.DefaultActivityTimeout = TimeSpan.FromHours(24);
```

### Checking for Pending Approvals (External System)

Poll the workflow from a UI, monitoring tool, or approval service:

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("MyAgent", userId);

DurableApprovalRequest? pending = await client.GetPendingApprovalAsync(sessionId);

if (pending is not null)
{
    Console.WriteLine($"Pending approval: {pending.Description}");
    Console.WriteLine($"RequestId: {pending.RequestId}");
}
```

### Resolving a Decision (External System)

```csharp
var result = await client.ResolveApprovalAsync(
    sessionId,
    new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Reviewed and approved by operations team."
    });

Console.WriteLine($"Decision status: {result.Status}");
```

`ResolveApprovalAsync` unblocks the tool when its result is `Accepted`; an identical retry returns
`AlreadyResolved`, while a changed retry returns `Conflict`. `RequestApprovalAsync` in the tool returns a generic
`DurableApprovalDecision`.

Use `ITemporalAgentClient.CancelPendingApprovalAsync(sessionId)` and `ShutdownAsync(sessionId)` for lifecycle operations. The typed session ID keeps application resource lookup at the caller boundary. It is still a routing locator, not authorization: authenticate and authorize the application resource before constructing or accepting a session ID.

### Workflow-Parked Approval (compute-free, multi-day waits)

For long approval windows or cost-sensitive workloads where pinning an activity slot is undesirable, use the
workflow-parked flavor: the turn loop itself parks, no activity is held open, and the workflow resumes only after
`ResolveApprovalAsync` is called. Triggered two ways:

- `agent.AddTool(tool, opts => opts.RequireApproval())` — absolute floor; always parks before the tool runs.
- `IAgentToolInterceptor` returning `DurableToolDecision.PauseForApproval(description)` — dynamic, interceptor-driven.

Register an interceptor per agent (`agent.AddToolInterceptor(sp => ...)`) or as a worker-level default
(`opts.DefaultToolInterceptor`). `ResolveApprovalAsync` is the same external API for both flavors.

See [HITL Patterns](./hitl-patterns.md) for the full guide including the two-flavor comparison table and testing patterns.

---

## Scheduling

Four primitives cover every proactive agent invocation pattern. They all run `AgentJobWorkflow` —
a lightweight, fire-and-forget workflow with no conversation history, no StateBag, and no TTL loop.
Results are visible in the Temporal Web UI; to capture output, start a regular agent session from
inside the job using `TemporalAgentContext`.

| Primitive                                         | Context           | Recurrence              |
|---------------------------------------------------|-------------------|-------------------------|
| `AddScheduledAgentRun`                            | Config time       | Recurring               |
| `ITemporalAgentClient.ScheduleAgentAsync`         | Runtime           | Recurring               |
| `ScheduleActivities.ScheduleOneTimeAgentRunAsync` | Inside a workflow | One-time                |
| `ITemporalAgentClient.RunAgentDelayedAsync`       | External caller   | One-time (full session) |

### Recurring Schedules

#### Config-time registration

Declare scheduled runs inside `AddTemporalAgents`. The `ScheduleRegistrationService` creates them
automatically when the worker starts. If the schedule already exists (e.g., on subsequent restarts)
a warning is logged and the existing schedule is left untouched.

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SummaryAgent", agent =>
        {
            agent.Instructions = "Summarize the day's activity report.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddScheduledAgentRun(
            agentName: "SummaryAgent",
            scheduleId: "daily-summary",
            request: new RunRequest("Summarize today's activity report."),
            spec: new ScheduleSpec
            {
                Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromDays(1))]
            });
    });
```

#### Programmatic scheduling

Call `ScheduleAgentAsync` at any time to create a Temporal Schedule. The returned `ScheduleHandle`
lets you pause, trigger, update, or delete the schedule:

```csharp
ITemporalAgentClient client = // resolved from DI

ScheduleHandle handle = await client.ScheduleAgentAsync(
    agentName: "ReportAgent",
    scheduleId: "weekly-report",
    request: new RunRequest("Generate the weekly metrics report."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec { Hour = [new ScheduleRange(9)], DayOfWeek = [new ScheduleRange(1)] }
        ]
    });

// Trigger immediately (outside the normal cadence)
await handle.TriggerAsync();

// Pause and resume
await handle.PauseAsync(note: "Pausing during maintenance window.");
await handle.UnpauseAsync();

// Retrieve an existing handle by ID
ScheduleHandle existing = client.GetAgentScheduleHandle("weekly-report");
await existing.DeleteAsync();
```

> **Schedule orphaning**: Temporal Schedules are independent of workers. Removing an agent from
> `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. Always call
> `DeleteAsync()` via `GetAgentScheduleHandle` when decommissioning a scheduled agent.

> **Config drift**: if you change a schedule's spec in code, the change is silently skipped on
> restart (the existing schedule is kept). To apply the updated spec, delete the schedule first via
> `GetAgentScheduleHandle`, then restart the worker.

---

### Deferred One-Time Runs

#### From inside an orchestrating workflow

Use `ScheduleActivities.ScheduleOneTimeAgentRunAsync` to schedule a future agent run from within a
`[WorkflowRun]` method. This uses Temporal's `StartDelay` — a single workflow execution is created
with a delayed start, leaving no persistent schedule entity behind after it completes.

```csharp
[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string topic)
    {
        // Run the main analysis immediately
        var analyst = TemporalWorkflowExtensions.GetTemporalAgent("AnalystAgent");
        var session = await analyst.CreateSessionAsync();
        await analyst.RunAsync($"Analyze: {topic}", session);

        // Schedule a follow-up comparison in 7 days — fire-and-forget, no blocking
        await Workflow.ExecuteActivityAsync(
            (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
            {
                AgentName = "AnalystAgent",
                RunId     = $"followup-{topic}",
                Request   = new RunRequest($"Compare today's findings on '{topic}' against last week's."),
                RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7)
            }),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }
}
```

The activity is idempotent on retry: `WorkflowIdConflictPolicy.UseExisting` ensures that a second
`StartWorkflowAsync` call (after a crash-before-ack) finds the already-scheduled execution and
returns normally. If `RunAt` is in the past when the activity executes, the run starts immediately.

#### From an external caller

`RunAgentDelayedAsync` defers the start of a **full agent session** (`AgentWorkflow`, with
conversation history and StateBag). It is intended for external callers, not workflow code.

```csharp
ITemporalAgentClient client = // resolved from DI

var sessionId = new TemporalAgentSessionId("OnboardingAgent", userId);

// Workflow is created now but does not start executing for 24 hours
await client.RunAgentDelayedAsync(
    sessionId,
    new RunRequest("Welcome! Your trial period has started. How can I help you get set up?"),
    delay: TimeSpan.FromHours(24));
```

> **Known limitation**: if a workflow with the same session ID is already running (`UseExisting`
> policy), `StartDelay` is ignored and the existing workflow is reused immediately. This method
> only applies the delay when starting a brand-new session.

---

## MCP Tool Integration

[Model Context Protocol](https://modelcontextprotocol.io/) servers expose `McpClientTool` objects,
which already derive from `AIFunction`. Connect and enumerate tools asynchronously before building
the host. Keep the client alive for the worker lifetime; do not store it in workflow state.

```csharp
await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://mcp.example.com"),
    Name = "inventory",
}));

IList<McpClientTool> discovered = await mcp.ListToolsAsync();

var byName = discovered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
var lookup = byName.TryGetValue("lookup_inventory", out var read)
    ? read
    : throw new InvalidOperationException("Required MCP tool is missing.");
var delete = byName.TryGetValue("delete_inventory", out var write)
    ? write
    : throw new InvalidOperationException("Required MCP tool is missing.");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("McpAgent", agent =>
        {
            agent.Instructions = "You can call the configured MCP tools.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(lookup);
            agent.AddTool(delete, policy => policy.NoRetry().RequireApproval());
        });
    });
```

Dynamic discovery is appropriate only when the whole authenticated server catalog is trusted. For
production allowlists, construct `McpClientTool` from reviewed, checked-in `Protocol.Tool`
definitions and use exact ordinal lookup. That pins the model-visible schema; it does not
authenticate the server or prove the remote implementation is compatible. See the complete
[MAF MCP guide](mcp-tools.md) and runnable [McpTools sample](../../../samples/MAF/McpTools).

---

## Context Providers and External Memory

`AIContextProvider` instances run before each LLM call inside `AgentActivities`. Compatible providers
contribute retry-safe instructions or messages and store compact per-session state in
`AgentSessionStateBag`; see [Individual MAF Context Providers](individual-context-providers.md) and
the [bounded compatibility contract](../../architecture/MAF/bounded-durable-agent-compatibility.md).

Provider-owned history and external writes, including direct Mem0-style registrations, are not
supported durable registrations. Their lifecycle hooks run in retryable LLM-step activities and
the library has no atomic idempotent provider-history contract. Do not use `AddContextProvider` as
a durable external-memory adapter.

`AddContextProvider` accepts either a concrete instance or an activity-scoped DI factory. Neither
form is session storage. The factory may run on every LLM-step activity attempt, including retries;
the instance object is likewise not a durable session object. Declare provider tools statically
with `IDurableToolSource` or the `durableTools` overload.

---

## Per-Tool Activity Configuration

Every tool registered via `agent.AddTool(...)` is dispatched as a Temporal activity (`TemporalCommunity.Extensions.Agents.InvokeAgentTool`). An explicit worker-level `opts.DefaultRetryPolicy` is inherited exactly. When it is null, tools use the library's bounded five-attempt default with exponential backoff capped at 30 seconds. Override per tool via the `configure` callback on `AddTool`.

| Property / Method on `DurableToolOptions` | Purpose |
|---|---|
| `StartToCloseTimeout` | Per-tool activity timeout. `null` inherits worker default. |
| `HeartbeatTimeout` | Per-tool heartbeat timeout. `null` inherits worker default. |
| `RetryPolicy` | Per-tool retry policy. `null` inherits worker default. |
| `NoRetry()` | Sugar for `RetryPolicy = new() { MaximumAttempts = 1 }`. Use on write tools. |
| `WithMaxAttempts(int n)` | Sugar for fixed-attempt retry. |
| `WithTimeout(TimeSpan t)` | Sugar for `StartToCloseTimeout`. |

`agent.MaxToolCallsPerTurn` (default `20` when not set) caps step-loop iterations per single agent turn. The value propagates from the agent's registration into session-based workflows, scheduled jobs, and sub-agent calls via `GetTemporalAgent()` — you configure it once on the builder and it takes effect everywhere. When exceeded, the workflow returns a structured "iteration cap exceeded" assistant message rather than letting workflow history grow unbounded.

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.Instructions = "You help customers with support requests.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // Read tool — inherits the worker default retry policy.
            agent.AddTool(lookupOrderTool);

            // Write tool — bind NoRetry() to the AIFunction reference. Cannot mistype the name.
            agent.AddTool(sendEmailTool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

            agent.MaxToolCallsPerTurn = 10;
        });
    });
```

For the canonical write-vs-read tool example, the per-tool retry hierarchy, and what the Temporal Web UI shows, see [Durable Agents](./durable-agents.md).

To add a pre-dispatch lifecycle hook — for risk scoring, PII scrubbing, argument enrichment, or dynamic approval gates — register an `IAgentToolInterceptor`. See [Tool Interceptor](./tool-interceptor.md) for the full guide.

---

## OpenTelemetry Integration

The library always emits a Temporal `agent.turn` correlation span that composes with the Temporal
SDK interceptor. Optional MAF/MEAI OpenTelemetry middleware emits a canonical GenAI child span.

### Setup

Install `Temporalio.Extensions.OpenTelemetry` alongside your preferred OTel exporter, then register both the Temporal
tracing interceptor and the agent activity source:

```csharp
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using TemporalCommunity.Extensions.Agents;

const string mafTelemetrySource = "MyCompany.MyAgent";

// 1. Configure the OTel tracer provider with all relevant sources
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,      // Temporal client spans (StartWorkflow, etc.)
        TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
        TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans (RunActivity)
        TemporalAgentTelemetry.ActivitySourceName, // Temporal correlation spans
        mafTelemetrySource)                        // optional MAF canonical GenAI spans
    .AddOtlpExporter()
    .Build();

// 2. Add the tracing interceptor to the Temporal client
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost  = "localhost:7233";
    opts.Interceptors = new[] { new TracingInterceptor() };
});

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.ConfigureAgentPipeline = pipeline =>
                pipeline.UseOpenTelemetry(mafTelemetrySource);
        });
    });
```

### Span Hierarchy

A single model step with MAF telemetry produces this span subtree:

```
agent.client.send          (DefaultTemporalAgentClient — before the Update reaches Temporal)
  └── StartWorkflow / RunActivity   (Temporal SDK spans via TracingInterceptor)
        └── agent.turn     (Temporal correlation, always present)
              └── invoke_agent (MAF canonical GenAI span and usage owner)
```

| Span                | Source                                      | Key Attributes                                                                                                                  |
|---------------------|---------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| `agent.client.send` | `TemporalAgentTelemetry.ActivitySourceName` | `gen_ai.agent.name`, `gen_ai.conversation.id` |
| `agent.turn` | `TemporalAgentTelemetry.ActivitySourceName` | `gen_ai.agent.name`, `gen_ai.conversation.id`, `temporal.agent.correlation_id`; fallback `gen_ai.usage.*` only without upstream telemetry |
| `invoke_agent` | Configured MAF source | Canonical MAF GenAI attributes and usage plus `temporal.agent.correlation_id` when sampled |
| SDK spans           | `TracingInterceptor.*Source`                | Standard Temporal attributes                                                                                                    |

A standalone MEAI `OpenTelemetryChatClient` also owns usage, but its chat span is created below
the session boundary. It shares the `agent.turn` trace and is correlated by trace identity; tags do
not inherit, so the Temporal correlation attribute is not copied to that child.

The span name constants are available on `TemporalAgentTelemetry`:

```csharp
TemporalAgentTelemetry.ActivitySourceName    // "TemporalCommunity.Extensions.Agents"
TemporalAgentTelemetry.AgentTurnSpanName     // "agent.turn"
TemporalAgentTelemetry.AgentClientSendSpanName // "agent.client.send"
```

### Search Attributes

Search attribute upserts are **on by default**. `AgentWorkflow` upserts three [custom search attributes](https://docs.temporal.io/visibility#custom-search-attributes)
on each workflow, enabling operational queries in the Temporal Web UI and via `ListWorkflowsAsync`:

| Attribute          | Type           | Description                                            |
|--------------------|----------------|--------------------------------------------------------|
| `AgentName`        | Keyword        | The registered agent name                              |
| `SessionCreatedAt` | DateTimeOffset | When the workflow first started                        |
| `TurnCount`        | Long           | Number of completed agent responses in this session    |

Example queries in the Temporal UI:

```
AgentName = "BillingAgent" AND TurnCount > 10
SessionCreatedAt > "2026-03-01T00:00:00Z"
```

> **Production clusters:** search attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) must be
> pre-registered on your Temporal cluster before workers start. On `temporal server start-dev` they
> are registered automatically. On production clusters, register them once using the Temporal CLI:
>
> ```bash
> temporal operator search-attribute create --name AgentName --type Keyword
> temporal operator search-attribute create --name SessionCreatedAt --type Datetime
> temporal operator search-attribute create --name TurnCount --type Int
> ```
>
> Set `opts.EnableSearchAttributes = false` to disable search attribute writes if your cluster does
> not have these attributes registered.
