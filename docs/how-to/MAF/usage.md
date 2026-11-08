# MAF Usage Reference

`TemporalCommunity.Extensions.Agents` makes a Microsoft Agent Framework (`Microsoft.Agents.AI`)
`AIAgent` durable with Temporal. It builds on `TemporalCommunity.Extensions.AI`, adding named
agents, `AgentSessionStateBag`, `AIContextProvider` support, and Temporal Web UI search attributes.
The preferred worker registration path is `Agent`, which accepts standard MAF
`ChatClientAgentOptions` and an optional per-agent callback that configures `TemporalAgentOptions`.
The lower-level
`AddDurableAgent` builder remains available for advanced Temporal composition. There are two ways to
interact with a registered agent:

- **From outside a workflow** — external callers (ASP.NET, console, background service) resolve a
  `TemporalAIAgentProxy` via `GetTemporalAgentProxy`.
- **From a regular workflow** — schedule an activity that resolves and invokes the registered
  `TemporalAIAgentProxy`; pass a workflow-generated session ID and request ID so activity retries
  reuse the same agent turn.

**New here? Start with the [Quickstart](./quickstart.md)** — one agent registered and called, in
about thirty lines. This page is the full reference: every builder slot, option, and behaviour. To choose between it and
`TemporalCommunity.Extensions.AI`, see the [Library Combinations Guide](../../library-combinations.md);
for a runnable starting point, see the [Sample Catalog](../../../samples/catalog.md). For externally
reachable session or approval endpoints, apply the normative [security boundary](../../security.md)
before calling a durable client.

---

## Registration

`Agent` is the preferred worker-hosted registration path. It accepts a MAF
`ChatClientAgentOptions` plus an optional callback that configures per-agent Temporal settings.
The callback receives a fresh `TemporalAgentOptions` and can set execution values, tool middleware,
and memory configuration. When omitted, Temporal settings use their defaults. The client-only
counterpart is
`AddTemporalAgentProxies(taskQueue, agentNames...)`, which registers keyed `AIAgent` proxies without
registering the worker or agent implementation. `AddAgentProxy` is still available for client-only
registrations that need per-agent proxy settings.

The compact MAF path transfers `Description`, `ChatOptions`, `Tools`, and the ordered
`AIContextProviders` from `ChatClientAgentOptions`. The unkeyed `IChatClient` resolves from the
activity scope for each model call. `TemporalAgentOptions` holds Temporal-only values such as
`TimeToLive`, `ActivityTimeout`, `HeartbeatTimeout`, `ApprovalTimeout`, retry settings, and history
bounds. Custom `ChatHistoryProvider` and MAF options that would start a second tool loop or require
non-durable per-call behavior are rejected.

### MAF-options worker example

```csharp
static string GetWeather(string city, IServiceProvider services) =>
    services.GetRequiredService<WeatherService>().GetForecast(city);

var weatherTool = AIFunctionFactory.Create(
    GetWeather,
    name: "get_weather",
    description: "Returns a forecast for a city.");

builder.Services.AddScoped<WeatherService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(agents =>
        agents.Agent(
            "WeatherAgent",
            new ChatClientAgentOptions
            {
                Description = "Answers weather questions.",
                ChatOptions = new ChatOptions
                {
                    Instructions = "Use get_weather for forecasts.",
                    Tools = [weatherTool],
                },
            },
            temporal => temporal.TimeToLive = TimeSpan.FromHours(24)));
```

`IServiceProvider` parameters in `AIFunctionFactory` functions are bound from
`AIFunctionArguments.Services`; the library supplies the invocation's activity scope. Do not capture
a scoped service in a tool instance at registration time.

### Migrating from the builder API

The former builder surface remains available for settings that do not map to MAF agent options.
For the common configuration, move the corresponding values as follows:

| `DurableAgentBuilder` | MAF / Temporal registration |
|---|---|
| `Description` | `ChatClientAgentOptions.Description` |
| `Instructions` | `ChatClientAgentOptions.ChatOptions.Instructions` |
| `AddTool(AIFunction)` | `ChatClientAgentOptions.ChatOptions.Tools` |
| `AddContextProvider(provider)` | `ChatClientAgentOptions.AIContextProviders` |
| `ChatClient = sp => sp.GetRequiredService<IChatClient>()` | `AddChatClient(...)` registration in DI |
| `TimeToLive`, activity/approval timeouts, retry, history bounds | `Action<TemporalAgentOptions>` callback |

Use `AddDurableAgent` when you need builder-only integration points such as custom pipeline
configuration or tool-interceptor factories. Both paths use workflow-owned history and the same
durable activity boundaries.

### Supported MAF options

The compact registration path preserves these MAF inputs:

| MAF input | Temporal behavior |
|---|---|
| `ChatClientAgentOptions.Name` | Optional; when supplied, it must match the Temporal name, case-insensitively. |
| `Description` | Preserved as the agent description. |
| `ChatOptions` | Cloned and forwarded to model calls. `Instructions` becomes the registered agent instruction; the tool list is replaced by the durable tools below. |
| `ChatOptions.Tools` | Only `AIFunction` instances are accepted. Each function is dispatched through a separate Temporal activity. |
| `AIContextProviders` | Preserved in the configured order. |
| Temporal configuration callback | Configures per-agent TTL, activity/heartbeat/approval timeouts, retry policy, history/context bounds, tool middleware, and supported memory settings. |

`ChatClientAgentOptions.Id` and a custom `ChatHistoryProvider` are rejected. The workflow owns the
session identity and all conversation history. `ChatOptions.ConversationId`, a continuation token,
and enabled background responses are also rejected because this integration requires completed
model responses and workflow-owned history. Other registration-time `ChatOptions` values are
forwarded. Temporal owns the tool loop and does not add MAF's in-process function-invocation
decorator; the worker's `IChatClient` must be registered without `.UseFunctionInvocation()`.

The following `ChatClientAgentOptions` switches are rejected when enabled because they conflict
with serial durable tool dispatch or per-turn workflow-owned history: `AllowConcurrentInvocation`,
`RequirePerServiceCallChatHistoryPersistence`, `EnableMessageInjection`,
`DisableApprovalNotRequiredFunctionBypassing`, `DisableApprovalResponseBinding`, and
`EnableInvocableFunctionBypassing`.

For invocation options, `AgentRunOptions.ResponseFormat` is supported. `TemporalAgentRunOptions`
also supports tool selection, fire-and-forget, correlation ID, and request ID. The
`ChatClientAgentRunOptions` path supports `ResponseFormat` only; its `ChatClientFactory` and other
run-time `ChatOptions` overrides are rejected. Unknown run-option subclasses and
`RunStreamingAsync` are explicitly unsupported.

> **Note:** the alternate three-argument `AddHostedTemporalWorker(address, namespace, queue)`
> overload configures a worker-internal client but does not register `ITemporalClient` in DI. If
> you use that overload instead of the separate `AddTemporalClient` call shown above,
> `AddTemporalAgents` (and anything else that needs `ITemporalClient`) will not find one — call
> `AddTemporalClient` explicitly first.

### Continue from here

| Need | Canonical guide |
| --- | --- |
| Select a package or sample | [Library Combinations Guide](../../library-combinations.md) and [Sample Catalog](../../../samples/catalog.md) |
| Use a client-only process | [Invoking Agents from External Code](#invoking-agents-from-external-code-proxy) |
| Configure tools, retries, and writes | [Durable Agents](durable-agents.md) |
| Write and register a context provider | [Context Providers](context-providers.md) |
| Understand supported MAF inputs | [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md) |
| Manage prompt/context size | [History & Token Optimization](prompt-caching.md) |
| Full builder/options reference | [Reference](#reference) below |

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
workflow waits deterministically for its typed input before processing that Update. Immediate
fire-and-forget uses a validated Update-With-Start. Delayed starts carry their initial request in
workflow input because the pinned SDK does not combine `WorkflowStartDelay` with Update-With-Start;
if the session already exists, the request is delivered immediately. Callers do not need a separate
start step.

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

The workflow owns the authoritative `AgentSession.StateBag`. The proxy returns the committed bag
and a monotonically increasing state revision as response metadata, then updates the session's
local cache only when that revision is newer. A cached bag is never sent back as workflow input:
serializing or resuming an old client session cannot overwrite workflow state. Callers cannot write
StateBag in this release; use it for workflow/provider/tool-owned session data, not caller-supplied
authorization or tenant identity. Supply trusted identity through worker-side services or explicit
request context instead.

Request-ID recovery is bounded by the workflow's retained terminal-outcome ledger (128 records and
1 MiB by default). Keep a caller-generated `TemporalAgentRunOptions.RequestId` if you need to retry
after an ambiguous response; a retry with the same ID and identical input replays the retained
outcome without re-running the turn. Oversized retained outcomes return a typed outcome-too-large
error with the committed revision and bounded tool-call summary (or counts-only summary when the
compact record also exceeds the limit). Once an outcome is evicted, a retry may be treated as a new
turn. Different requests are rejected while a turn is active with a retryable session-busy error;
retry with backoff rather than expecting the workflow to queue it.

### Request ID Recovery

Persist **both the session workflow ID and request ID before dispatch**. A restarted caller needs
both; correlation IDs do not deduplicate turns. This example writes an application-owned receipt
file without overwriting an existing one:

```csharp
var session = (TemporalAgentSession)await agentProxy.CreateSessionAsync();
var requestId = Guid.NewGuid().ToString("N");
await using (var file = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write))
{
    await JsonSerializer.SerializeAsync(file, new
    {
        SessionWorkflowId = session.SessionId.WorkflowId,
        RequestId = requestId,
    });
    file.Flush(flushToDisk: true);
}
var response = await agentProxy.RunAsync(
    "What is the capital of France?",
    session,
    new TemporalAgentRunOptions { RequestId = requestId });
```

After an ambiguous acknowledgement (for example, `WorkflowUpdateRpcTimeoutOrCanceledException`),
reload the receipt instead of sending again. Poll only `Pending`, with backoff and a bounded RPC wait:

```csharp
using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
var sessionId = TemporalAgentSessionId.Parse(receipt.RootElement.GetProperty("SessionWorkflowId").GetString()!);
var requestId = receipt.RootElement.GetProperty("RequestId").GetString()!;
var temporalAgentClient = serviceProvider.GetRequiredService<ITemporalAgentClient>();
var delay = TimeSpan.FromMilliseconds(250);
for (var attempt = 0; attempt < 6; attempt++)
{
    TemporalAgentTurnOutcome outcome;
    try
    {
        using var queryWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        outcome = await temporalAgentClient.GetTurnOutcomeAsync(sessionId, requestId, queryWait.Token);
    }
    catch (Exception error) when (error is RpcException or WorkflowQueryFailedException
        or WorkflowQueryRejectedException or OperationCanceledException)
    {
        Console.Error.WriteLine($"Query failed; no outcome established: {error.Message}");
        throw;
    }
    if (outcome.Status != TemporalAgentTurnOutcomeStatus.Pending)
    {
        if (outcome.Status == TemporalAgentTurnOutcomeStatus.Unavailable)
            Console.Error.WriteLine("No retained outcome; reconcile authoritative state. Do not resubmit.");
        else
            Console.WriteLine($"{outcome.Status}; tool counts: {outcome.ToolCallCounts}");
        return;
    }
    if (attempt < 5)
    {
        await Task.Delay(delay);
        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
    }
}
throw new TimeoutException("Still pending after six queries; retain both IDs for later recovery.");
```

The [BasicAgent recovery mode](../../../samples/MAF/BasicAgent/README.md#recover-an-ambiguous-dispatch)
runs this flow. `Succeeded`/`Failed` records expose the retained committed revision and bounded
tool-call summaries/counts. `DetailsOmitted` is true if response/failure details or tool identities
were omitted; counts can still describe omitted calls. `FailureNonRetryable` is null when no
classification is retained. A retryable classification does not prove an external tool effect is safe to repeat.

`GetTurnOutcomeAsync` is a read-only query and never starts or resubmits a turn. Terminal outcomes
are bounded by the workflow's record-count and byte limits; an evicted or never-recorded request is
`Unavailable`, not `Pending`; it is not evidence the turn never ran. A query NotFound with the
server's typed `NotFoundFailure` detail is also `Unavailable`. That generic detail does not name
the missing resource, so this mapping relies on Temporal's workflow-query error contract;
namespace-not-found and unknown or absent error details propagate. Use the same `RequestId` for a deliberate retry only after reconciliation;
while the outcome remains retained, resubmitting the same input returns the recorded terminal result.

Within one workflow run, a matching request ID and fingerprint joins an active turn or returns
its recorded terminal outcome. A different request is rejected as `Busy`; reusing the ID with
changed effect-bearing input is rejected as `Conflict`, before the update is accepted into history.
Up to 128 terminal outcomes / 1 MiB of serialized outcomes are retained by default and carried
through Continue-as-New, along with the opaque session epoch and cumulative completed-turn count.
An oversized outcome remains inside the deduplication window while its compact or minimal record
is retained. Only evicted or never-recorded outcomes are outside that window. Request IDs do not
make downstream tool effects exactly-once.

Failed turns throw `DurableTurnFailedException` with the last committed revision and a bounded,
result-free `ToolCallSummaries` list. A completed call means the function returned, a failed call
means a classified failure or no dispatch, and an uncertain call means dispatch happened without
an observed result (for example, a timeout). Uncertain does not mean no effect occurred.
The summary retains the newest identities within 32 entries and 16 KiB; `ToolCallCounts` accounts
for all calls, and `OmittedToolCallCounts` accounts for those whose identities were omitted.
StateBag and provider/approval state roll back, but completed external effects do not. An
interrupted-turn record remains in conversation history so the next turn sees that dispatch occurred.

A retained **full outcome** replays the typed failure. A **compact record** replays
`DurableTurnOutcomeTooLargeException` with retained identities/statuses and omitted counts; a
**minimal record** returns counts only. No level reconstructs identities it did not retain, and none
redispatches the turn while retained. Inspect the outcome and reconcile external effects before
submitting a new request ID.

The fingerprint covers messages, the configured model route and tool authority, tool selection,
and structured-output settings. Correlation IDs and Temporal Update IDs remain separate from this
logical request identity.

### Cancel an active turn

Resolve `ITemporalAgentClient` for workflow-owned cancellation. The request ID must be the same ID
supplied to `RunAsync`; cancellation is a separate update and does not undo external effects:

```csharp
var agentClient = services.GetRequiredService<ITemporalAgentClient>();
var requestId = Guid.NewGuid().ToString("N"); // Persist before submission.
var turn = agentProxy.RunAsync(
    "Run the long operation.",
    session,
    new TemporalAgentRunOptions { RequestId = requestId });

var cancellation = await agentClient.CancelTurnAsync(session.SessionId, requestId);
if (cancellation.Status == DurableTurnCancellationStatus.CancellationRequested)
{
    Console.WriteLine("The workflow requested cancellation.");
}
```

Observe the original `turn` task for its terminal outcome. A cancellation race can return
`AlreadyCompleted`; if the request has already completed or does not match the active turn, update
validation rejects it before acceptance. The workflow propagates cancellation to an active model,
interceptor, or tool activity and interrupts a pending approval wait. Temporal waits for activity
cancellation to complete before stopping and schedules no further activity work. Activity code and
downstream operations must cooperate with cancellation. Cancellation cannot roll back a dispatched
external effect, so an interrupted effect may have an uncertain outcome; use downstream idempotency
and reconcile authoritative state. A cancellation token passed to `RunAsync` or `CancelTurnAsync`
cancels only that RPC wait; it does not cancel an accepted turn. Use `CancelTurnAsync` to request
turn cancellation.

If a tool activity was dispatched before a turn fails or is cancelled, the next model step receives
a bounded interrupted-turn projection: completed call/result pairs are retained, uncertain
dispatched outcomes are marked unknown, and undispatched calls are marked not executed. Partial
assistant prose is dropped. This projection supports protocol-valid continuation but is not an
external-effect audit or proof of rollback; reconcile with the system of record before retrying.

---

## Reducing the LLM Context Window

For long-running agent sessions the conversation history accumulated in the
`AgentWorkflow` can grow large enough to make each LLM call expensive.
`TemporalCommunity.Extensions.Agents` can use the same MEAI `IChatReducer` family
as `TemporalCommunity.Extensions.AI`. Register the reducer in worker DI and select
it with `DefaultContextReducerKey` or the per-agent `ContextReducerKey`. The reducer
runs in the model activity on isolated copies of eligible older turns; it never
changes the retained history returned by `GetHistory`.
The key selects a worker-registered reducer; it does not snapshot the reducer implementation into
workflow history. Keep that keyed registration stable across retries and the lifetime of sessions
that use it, or use a new key when deliberately deploying a different context policy.

`MessageCountingChatReducer` is annotated `[Experimental("MEAI001")]`, so it needs a suppression
until MEAI stabilizes the type — the pragma below, or `<NoWarn>$(NoWarn);MEAI001</NoWarn>` in the
project file.

```csharp
#pragma warning disable MEAI001
builder.Services.AddKeyedSingleton<IChatReducer>(
    "recent-context",
    new MessageCountingChatReducer(20));
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.DefaultContextReducerKey = "recent-context";
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxModelContextTokens = 8192;
            agent.ModelOutputTokenReserve = 1024;
        });
    });
#pragma warning restore MEAI001
```

With this configuration:

- The `AgentWorkflow`'s `_history` retains every message ever exchanged in the
  session — that is the durable, replay-safe source of truth and survives worker
  restarts and continue-as-new transitions.
- The reducer receives only eligible prior turns. The active turn, system/developer
  instructions, approval turns, and interrupted turns are protected from reduction.
- Reduction runs before budget fitting, so eligible prior context can be reduced before
  the worker considers trimming complete turns. Reducer output is validated for complete
  tool-call/result exchanges and checked against the final model-context budget. Invalid output or output that still exceeds
  the configured limit fails before the provider is called.
- Querying `_history` (e.g., for audit) still returns the full unreduced log.
- `MaxModelContextTokens` optionally bounds estimated input plus `ModelOutputTokenReserve`.
  After reduction, the worker trims only complete eligible prior turns before context providers
  run, then validates the final messages, instructions, tools, and response schema after provider contributions. If
  provider-added or protected content still exceeds the limit, the model activity fails with a
  non-retryable `DurableContextBudgetExceeded` outcome instead of silently omitting content.

The default estimator is a deterministic provider-independent heuristic, not an exact tokenizer.
Register `IDurableChatTokenEstimator` in worker DI for model-specific estimates. In split or
profiled deployments, the estimator and configured limit should match the worker-side model route.

> **Design rationale:** Conversation history lives on the workflow itself, where
> it is replay-safe via Temporal event history. Reducers shape only what is sent
> to the LLM per turn — they never own conversation state.

> **Note:** `MessageCountingChatReducer` is provided by the MEAI library
> (`Microsoft.Extensions.AI`). Any `IChatReducer` implementation works —
> token-counting reducers, summarization reducers, etc. — as long as it is
> deterministic for the same input and safe for concurrent activity calls. A reducer
> may run again when an activity retries, and any provider calls it makes are activity
> work with their own retry/cost implications. This is prompt shaping only: an `IChatReducer`
> is not a durable `DurableSessionEntry` compaction adapter. Use the keyed
> `HistoryReducerKey` path for continue-as-new history reduction.

See the equivalent guidance for `TemporalCommunity.Extensions.AI` in
[the MEAI usage guide](../MEAI/usage.md).

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

Start with MAF's own `AIAgent.RunAsync<T>`. It generates a JSON Schema from `T`, constrains the
model to it, and returns `AgentResponse<T>` whose `.Result` deserializes:

```csharp
var session = await agentProxy.CreateSessionAsync();

AgentResponse<WeatherReport> response = await agentProxy.RunAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session);

WeatherReport report = response.Result;
```

### Using `RunStructuredAsync<T>`

Reach for this library's `RunStructuredAsync<T>` when the model wraps its JSON in markdown fences or
adds prose — MAF's version throws on those — or when you want a corrective retry. It calls MAF's
typed run underneath, so the generated schema still reaches the model, then strips fences and feeds
any parse error back for self-correction. It returns `T` directly:

```csharp
WeatherReport report = await agentProxy.RunStructuredAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session);
```

Control retry behavior with `StructuredOutputOptions`:

```csharp
var report = await agentProxy.RunStructuredAsync<WeatherReport>(
    [new ChatMessage(ChatRole.User, "What's the weather in Seattle?")],
    session,
    new StructuredOutputOptions
    {
        MaxRetries = 3,                // default: 2
        IncludeErrorContext = true,     // default: true — appends error details to retry prompt
        JsonSerializerOptions = myOpts  // default: null — uses web defaults (camelCase)
    });
```

Available on the registered proxy:

```csharp
// Via the client
var result = await client.RunStructuredAsync<WeatherReport>(sessionId, request);
```

The name is deliberate: MAF declares `RunAsync<T>` as an instance method, which would shadow an
extension of the same name. See [Structured output](./structured-output.md).

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

## Invoking Agents from a Workflow

Workflow code must not invoke an agent directly. Resolve the registered proxy inside an activity,
and generate the session and request IDs in workflow code. Temporal replays those IDs unchanged, so
an activity retry uses the same session and request identity. For example, see
[`WorkflowOrchestration`](../../../samples/MAF/WorkflowOrchestration/README.md).

```csharp
[Workflow]
public sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var sessionId = WorkflowAgents.NewAgentSessionId("ResearcherAgent");
        var requestId = Workflow.NewGuid().ToString("N");
        return await Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                "ResearcherAgent", sessionId.WorkflowId, requestId, $"Research: {topic}"),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
    }
}
```

The activity resolves the proxy from its scoped services, creates a
`TemporalAgentSession` from the passed session ID, and supplies the stable request ID through
`TemporalAgentRunOptions.RequestId`. The registered `AgentWorkflow` owns conversational history
and carries it across its own Continue-as-New transitions.

---

## Invoking Agents from External Code (Proxy)

Use `TemporalAIAgentProxy` to interact with a registered agent from outside a Temporal workflow —
for example, from an ASP.NET handler, a background service, a console application, or an activity.
The proxy communicates with the running `AgentWorkflow` via Temporal workflow updates.

`TemporalAIAgentProxy` is `internal`; callers always reference it as `AIAgent` (MAF's base class). Resolution is always
via `services.GetTemporalAgentProxy("Name")`.

| Receiver | Where to use it | History owner |
|---|---|---|
| `AIAgent` resolved with `GetTemporalAgentProxy("Name")` | External code or activity code | The target `AgentWorkflow` |

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

The proxy's `CreateSessionAsync` and `RunAsync` signatures follow MAF's `AIAgent` API. Retain the
session across calls to preserve conversation history:

```csharp
AIAgent agentProxy = services.GetTemporalAgentProxy("SupportAgent");

var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("My order hasn't arrived.", session);
Console.WriteLine(r1.Messages[0].Text);

var r2 = await agentProxy.RunAsync("The order number is 12345.", session);
Console.WriteLine(r2.Messages[0].Text);  // Context from r1 is preserved
```

For regular workflows, schedule an activity that resolves and invokes the registered proxy, as
described in [Invoking Agents from a Workflow](#invoking-agents-from-a-workflow).

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

Sessions expire after the configured idle lifetime (default: 14 days). The workflow tracks an
absolute `Workflow.UtcNow` deadline, renews it on admitted/completed work, and carries it through
Continue-as-New. Active turns and approval waits drain before expiry. Configure per-agent overrides
on the builder:

```csharp
opts.AddDurableAgent("ShortLivedAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.TimeToLive = TimeSpan.FromHours(1);
});

// Or configure the default for all agents on this worker
opts.DefaultTimeToLive = TimeSpan.FromDays(7);
```

When the idle deadline elapses without active work, the `AgentWorkflow` completes naturally. The
next message to that session ID starts a fresh session lineage.

---

## Activity Timeouts

Every agent turn — one call to `RunAsync` — executes inside a Temporal activity. Two timeouts govern that activity:

| Option              | Default   | What it limits                                                                                           |
|---------------------|-----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
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

The registered proxy does not support `RunStreamingAsync`; it throws
`NotSupportedException`. The durable model-step activity uses MAF's non-streaming `RunAsync` and
returns the completed response. Use `RunAsync` and deliver that response from your application
boundary.

---

## Routing

Routing belongs inside your workflow, where every decision is durable, visible in history, and replayed from cache on crash recovery. The library provides two patterns:

- **Static routing** — a classifier agent runs inside the workflow and the result drives a switch statement with hardcoded agent names. Best for a fixed agent set.
- **Dynamic routing via activity** — the workflow discovers available agents by querying the descriptor registry inside an activity (whose result is cached in history). Best when the set of agents changes across deployments.

Both patterns are covered in detail in [Routing Patterns](./routing.md), with complete working code in `samples/MAF/WorkflowRouting/`.

---

## Parallel Agent Execution

For parallel fan-out, schedule one activity per agent and await them with
`Workflow.WhenAllAsync`. Each activity resolves its registered proxy. Generate a distinct stable
session ID and request ID for each call in workflow code.

```csharp
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents;

[Workflow]
public class ResearchAndSummarizeWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var researchSession = WorkflowAgents.NewAgentSessionId("ResearchAgent");
        var summarySession  = WorkflowAgents.NewAgentSessionId("SummaryAgent");
        var research = Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                researchSession.WorkflowId, Workflow.NewGuid().ToString("N"), $"Research: {topic}"),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
        var summary = Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                summarySession.WorkflowId, Workflow.NewGuid().ToString("N"), $"Summarize: {topic}"),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(35) });
        var results = await Workflow.WhenAllAsync([research, summary]);
        return $"Research: {results[0]}\n\nSummary: {results[1]}";
    }
}
```

Each proxy call starts or reuses its own registered `AgentWorkflow`; activity retry identity is
preserved by reusing the workflow-generated session and request IDs.

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
        CallId = pending.CallId,
        FunctionName = pending.FunctionName,
        Approved  = true,
        Reason    = "Reviewed and approved by operations team."
    });

Console.WriteLine($"Decision status: {result.Status}");
```

`ResolveApprovalAsync` unblocks the tool when its result is `Accepted`. Unknown, mismatched,
already-resolved, or conflicting decisions fail update validation before Temporal records an
accepted update. After an ambiguous RPC result, inspect the pending request and reconcile through
application-owned review state rather than treating a retry as a successful resolution.
`RequestApprovalAsync` in the tool returns a generic `DurableApprovalDecision`.

Use `ITemporalAgentClient.CancelPendingApprovalAsync(sessionId)` and `ShutdownAsync(sessionId)` for lifecycle operations. `GetHistoryBudgetAsync(sessionId)` reports the workflow's current history event/size usage and configured admission thresholds without adding history. The typed session ID keeps application resource lookup at the caller boundary. It is still a routing locator, not authorization: authenticate and authorize the application resource before constructing or accepting a session ID.
The history budget covers library-validated update operations, not arbitrary Temporal signals
sent directly by other clients. Temporal records an accepted signal even when the workflow has no
matching handler; restrict direct workflow access and use application/deployment access controls.

### Inspecting workflow history budget

The history-budget query is read-only and adds no workflow history:

```csharp
var status = await client.GetHistoryBudgetAsync(sessionId);
Console.WriteLine($"History events: {status.HistoryEventCount}/{status.HistoryEventSoftLimit}");
if (status.IsHistoryBlocked)
{
    var recovery = await client.RetryHistoryCompactionAsync(
        sessionId,
        operationId: Guid.NewGuid().ToString("N"));
    if (recovery.IsBlocked)
    {
        await client.TrimBlockedHistoryAsync(
            sessionId,
            operationId: Guid.NewGuid().ToString("N"));
    }
}
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/Usage/HistoryBudget.cs. -->

Agent history is retained workflow state, not an unlimited archive. `DefaultMaxEntryCount` (1000
by default) triggers Continue-as-New. MAF sessions can configure a worker/per-agent
`HistoryReducerKey` for a deterministic retained-history projection. There is no implicit suffix
trim: if the trigger is reached without a reducer, or the reducer fails/returns no progress, the
workflow retains its last valid history, closes new-turn admission, suspends idle expiry, and
reports `IsHistoryBlocked` in the history-budget query.
Reducers must preserve complete request/response turns and ordering, including every protected
current, approval, or interrupted turn; simple entry-count slicing is rejected.

While blocked, `RetryHistoryCompactionAsync` retries the configured keyed reducer; it cannot
recover a session with no reducer configured. `TrimBlockedHistoryAsync` is the separately
authorized lossy fallback and records a truncation marker. Recovery accepts at most three retries
per run and nine per incident across Continue-as-New, plus one explicit trim per incident.
Operation IDs must be unique and persisted by the caller; duplicate, concurrent, and over-limit
operations are rejected before acceptance. If protected history or Temporal headroom prevents
recovery, the session remains blocked. The API does not authenticate the reviewer: authorize every
recovery call against application-owned session access.

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
            agent.AddTool(delete, policy => policy.RequireApproval());
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

Managed session workflows run `AIContextProvider.InvokingAsync` once in a context-preparation
activity before the model/tool loop, then reuse the recorded instructions/messages on each LLM
step. `InvokedAsync` runs at terminal turn completion. Compatible providers store compact
per-session state in `AgentSessionStateBag`; see [Context Providers](context-providers.md) and the
[bounded compatibility contract](../../architecture/MAF/bounded-durable-agent-compatibility.md).

Provider-owned history and external writes, including direct Mem0-style registrations, are not
supported durable registrations. Provider hooks can retry before their activity results are
recorded, and the library has no atomic idempotent provider-history contract. Do not use
`AddContextProvider` as a durable external-memory adapter.

`AddContextProvider` accepts either a concrete instance or an activity-scoped DI factory. Neither
form is session storage. The factory may run for preparation and completion activity attempts,
including retries. The instance object is
likewise not a durable session object. Declare provider tools statically with `IDurableToolSource`
or the `durableTools` overload.

---

## Tool Activity Ordering and Retry Policy

Every tool registered via `agent.AddTool(...)` is dispatched as a Temporal activity
(`TemporalCommunity.Extensions.Agents.InvokeAgentTool`). Tool calls from one model response run
serially in model order. Each tool activity gets one attempt by default. Retries are opted into via
the agent-level retry policy and apply uniformly to that agent's tools; only enable them when all
registered tools are safe to retry. See [Durable Agents](./durable-agents.md#retry-policy-hierarchy)
for the activity policy details.

`agent.MaxToolCallsPerTurn` (default `20` when not set) caps step-loop iterations per single managed agent turn. Configure it on the registered agent builder. When exceeded, the workflow returns a structured "iteration cap exceeded" assistant message rather than letting workflow history grow unbounded.

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(agents =>
        agents.Agent(
            "SupportAgent",
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = "You help customers with support requests.",
                    Tools = [lookupOrderTool, sendEmailTool],
                },
        },
        _ => { }));
```

Tool retries are agent-wide on this registration path. If these tools have different retry
requirements, register them on separate agents rather than applying a retry policy to only one
tool. For serial dispatch and retry/idempotency guidance, see [Durable Agents](./durable-agents.md).

To add a pre-dispatch lifecycle hook — for risk scoring, PII scrubbing, argument enrichment, or dynamic approval gates — register an `IAgentToolInterceptor`. See [Tool Interceptor](./tool-interceptor.md) for the full guide.

---

## OpenTelemetry Integration

The library always emits a Temporal `agent.turn` correlation span that composes with the Temporal
SDK interceptor. Optional MAF/MEAI OpenTelemetry middleware emits a canonical GenAI child span.

### Setup

Install `Temporalio.Extensions.OpenTelemetry` alongside your preferred OTel exporter, then register both the Temporal
tracing interceptor and the agent activity source:

```csharp
using Microsoft.Agents.AI;          // pipeline.UseOpenTelemetry — an AIAgentBuilder extension
using OpenTelemetry;                // Sdk.CreateTracerProviderBuilder
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
|--------------------|----------------|----------------------------------------------------------|
| `AgentName`        | Keyword        | The registered agent name                              |
| `SessionCreatedAt` | DateTimeOffset | When the workflow first started                        |
| `TurnCount`        | Long           | Number of completed agent responses in this session    |

Example queries in the Temporal UI:

```
AgentName = "BillingAgent" AND TurnCount > 10
SessionCreatedAt > "2026-03-01T00:00:00Z"
```

> **Production clusters:** search attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) must be
> pre-registered on your Temporal cluster before workers start — this is **not** automatic, even
> for a local `temporal server start-dev`. Register them once using the Temporal CLI:
>
> ```bash
> temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int
> ```
>
> Production clusters need the equivalent one-time `temporal operator search-attribute create`
> commands. Set `opts.EnableSearchAttributes = false` to disable search attribute writes if your
> cluster does not have these attributes registered.

---

## Reference

### `DurableAgentBuilder` reference

| Property / Method | Purpose |
|-------------------|---------|
| `Name` (read-only) | Case-insensitive agent name passed in to `AddDurableAgent`. |
| `Description` | Used in `GetAgentDescriptors()` for routing prompts. Optional. |
| `Instructions` | Agent system prompt. Library stamps onto every LLM call's `ChatOptions.Instructions`. Optional. |
| `ChatClient` | **Required.** `Func<IServiceProvider, IChatClient>` factory invoked at activity execution time to resolve the model's `IChatClient`. Throws `InvalidOperationException` if omitted. |
| `ChatOptions` | LLM-call template (Temperature, ResponseFormat, MaxOutputTokens, etc.). `Tools` and `Instructions` set on this property are ignored. |
| `AddTool(AIFunction tool, Action<DurableToolOptions>? configure = null)` | Registers a concrete `AIFunction`. Per-tool timeout and interceptor/approval settings via `configure`. |
| `AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure = null)` | DI-resolving tool factory. |
| `AddTools(params AIFunction[] tools)` | Bulk registration of concrete tools. |
| `AddContextProvider(AIContextProvider provider, IEnumerable<DurableToolRegistrationSpec>? durableTools = null)` / `AddContextProvider(Func<IServiceProvider, AIContextProvider>)` | Wires a provider into the managed session pipeline. `InvokingAsync` prepares context once per logical turn and `InvokedAsync` runs at turn completion; activity retries may repeat either hook. See [Context Providers](context-providers.md) for tool limits. |
| `TimeToLive`, `ApprovalTimeout`, `ActivityTimeout`, `HeartbeatTimeout` | Per-agent overrides. `null` inherits the worker-level default on `TemporalAgentsOptions`. |
| `RetryPolicy` | Agent-level retry policy. Applies to the `RunDurableAgentStep` activity and all tool and interceptor activities for the agent; this is the only supported retry opt-in for tools. |
| `MaxEntryCount`, `HistoryReducerKey` | Per-agent continue-as-new bounds and keyed reducer. Inherit worker defaults when unset. |
| `ContextReducerKey` | Per-agent MEAI context reducer key. Inherits `opts.DefaultContextReducerKey`; affects only the model request, not retained history. |
| `MaxModelContextTokens`, `ModelOutputTokenReserve` | Optional per-agent estimated model-context limit and output reserve. Inherit worker defaults when unset. |
| `MaxToolCallsPerTurn` | Cap on LLM-step iterations per managed agent turn (default `20` when not set). No worker-level fallback. The value is resolved from the agent registration when the worker resolves the agent configuration. |
| `AddToolInterceptor(Func<IServiceProvider, IAgentToolInterceptor> factory)` | Registers a pre-tool lifecycle hook. The interceptor runs before each `InvokeAgentTool` activity and returns `DurableToolDecision` (from `TemporalCommunity.Extensions.AI`): `Proceed`, `PauseForApproval`, `Skip`, or `Block`. See `opts.DefaultToolInterceptor` for a worker-level default. |

### `DurableToolOptions` reference

| Property / Method | Purpose |
|-------------------|---------|
| `StartToCloseTimeout`, `HeartbeatTimeout` | Per-tool activity timeout overrides. `null` inherits the worker/agent timeout. |
| `RetryPolicy` | Per-tool retry overrides are not supported. Tools use one attempt by default or the agent's explicit policy. |
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
| `agent.MaxModelContextTokens` | `opts.DefaultMaxModelContextTokens` |
| `agent.ModelOutputTokenReserve` | `opts.DefaultModelOutputTokenReserve` |
| `agent.HistoryReducerKey` | `opts.DefaultHistoryReducerKey` |
| `agent.ContextReducerKey` | `opts.DefaultContextReducerKey` |
| `agent.MaxToolCallsPerTurn` | *no worker fallback — defaults to `20`* |
| `agent.AddToolInterceptor(...)` | `opts.DefaultToolInterceptor` — worker-level fallback; overridden per agent via `AddToolInterceptor` |

Retry policy resolution for agent activities:

1. A run-level policy, when the run path supports one, applies to the agent's activities.
2. `agent.RetryPolicy` applies uniformly to the model step, tools, and interceptor.
3. `opts.DefaultRetryPolicy` is the worker-level policy when the agent does not override it.
4. With no explicit policy, model steps use the bounded model default and tools/interceptors use
   one attempt rather than Temporal's unlimited server default.

Agent- and run-level policies affect all tool activities on that path, not only model calls. If
tools need different retry behavior, separate them across agents or make every affected tool
idempotent. Full table in
[Durable Agents](./durable-agents.md#retry-policy-hierarchy).

### Custom agent middleware

To wrap each model-step attempt — timing, tracing, step-level short-circuiting — set
`agent.ConfigureAgentPipeline` (`Action<AIAgentBuilder>`), or `opts.DefaultConfigureAgentPipeline`
for every agent; the per-agent value wins when both are set.

```csharp
agent.ConfigureAgentPipeline = pipeline =>
    pipeline.Use((inner, services) =>
        new TimingAgent(inner, services.GetRequiredService<ILogger<TimingAgent>>()));
```

Wrappers must derive from `DelegatingAIAgent`, stay transparent, avoid `IDisposable`, and pass the
exact supplied session through. Full guidance, worked examples, and the rules the library enforces
live in **[Intercepting LLM Calls](./llm-call-interception.md)** — which also covers the other
interception layer, decorating the `IChatClient` your `agent.ChatClient` factory returns.

For per-tool MAF-shaped middleware, set `TemporalAgentOptions.ToolMiddleware` in the existing
`.Agent(...)` registration. It runs after required approval inside each scoped tool attempt,
supports serializable short-circuit/results, and cannot alter the approved call.
See [Tool middleware](llm-call-interception.md#tool-middleware) and the complete
[Middleware sample](../../../samples/MAF/Middleware/).

### Factory lifetimes

Tool factories resolve from the activity scope on each preparation/model/tool attempt; the worker
blueprint caches registration metadata, not scoped implementations. The chat-client,
context-provider, and interceptor factories also use the corresponding attempt's DI scope. Do not use factory or wrapper
fields as session storage: an attempt can retry, run on another worker, or overlap another session.
Use `AgentSession.StateBag` instead.

The library composes the chat pipeline internally and passes `UseProvidedChatClientAsIs = true` to
MAF, so `FunctionInvokingChatClient` is **not** auto-injected — the workflow owns the tool-dispatch
loop. Register a bare `IChatClient` and do not call `.UseFunctionInvocation()`. This is **enforced**:
a `FunctionInvokingChatClient` anywhere in the chat client your factory returns fails the activity
non-retryably, before the model is called. See
[Intercepting LLM Calls](./llm-call-interception.md#the-chat-client-layer).

For managed session workflows, `AIContextProvider.InvokingAsync` runs once before the model/tool
loop and `InvokedAsync` runs once when that logical turn reaches a terminal result. Their activities
can retry, so hook effects remain at-least-once; completed preparation output is replayed rather
than recalled. The registered agent workflow invokes per-step callbacks. See
[Context Providers](context-providers.md) for failure, cancellation, StateBag, and tool semantics.

For the workflow-loop semantics (per-tool fan-out, crash safety, continue-as-new) see [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../architecture/MAF/agent-sessions-and-workflow-loop.md).
For the supported MAF agent/provider boundary, see [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md).

---

## Where to go next

- [Using Temporal SDK plugins](../../library-combinations.md#using-temporal-sdk-plugins) — registering your own Temporal worker or client plugins alongside this library
**How-to guides**

- [Durable Agents](durable-agents.md) — the canonical write-vs-read tool example and per-agent retry policy
- [Context Providers](context-providers.md) — writing and registering an `AIContextProvider`
- [Agent Memory](memory.md) — how workflow history and provider-backed memory differ
- [HITL Patterns](hitl-patterns.md) — the two approval flavors, full guide and testing patterns
- [Routing Patterns](routing.md) — static and dynamic agent routing
- [Tool Interceptor](tool-interceptor.md) — pre-dispatch lifecycle hooks
- [MCP Tools](mcp-tools.md) — remote MCP client tool registration
- [Testing Agents](testing-agents.md) — unit vs. integration testing durable agents
- [Observability](observability.md) — the full OTel span hierarchy
- [History & Token Optimization](prompt-caching.md) — managing prompt/context size
- [Do's and Don'ts](dos-and-donts.md) — terse workflow-determinism quick reference

**Architecture & concepts**

- [Agent Sessions and Workflow Loop](../../architecture/MAF/agent-sessions-and-workflow-loop.md) — crash safety, continue-as-new, per-tool fan-out
- [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md) — the supported MAF agent/provider boundary
- [Durability and Determinism](../../architecture/MAF/durability-and-determinism.md) — Temporal's replay guarantees in the context of agent orchestration
- [Library Combinations Guide](../../library-combinations.md) — choosing between this package and `TemporalCommunity.Extensions.AI`
- [Security Boundary](../../security.md) — normative authentication/authorization rules for externally reachable session and approval endpoints
- [Sample Catalog](../../../samples/catalog.md) — choose a runnable MAF sample by intent
