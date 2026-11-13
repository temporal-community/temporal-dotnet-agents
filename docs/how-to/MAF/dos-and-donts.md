# Do's and Don'ts

A consolidated reference of common mistakes and best practices when building with TemporalAgents. Each entry explains the rule, why it matters, and what to do instead.

---

## Table of Contents

1. [Workflow Determinism](#workflow-determinism)
2. [Agent Registration and DI](#agent-registration-and-di)
3. [Session and History Management](#session-and-history-management)
4. [Activity Timeouts](#activity-timeouts)
5. [Observability](#observability)
6. [Testing](#testing)
7. [Security and Configuration](#security-and-configuration)
8. [Tool Activity Ordering and Retries](#tool-activity-ordering-and-retries)

---

## Workflow Determinism

These rules apply to any code inside a `[Workflow]` class — including `[WorkflowRun]`, `[WorkflowUpdate]`, `[WorkflowSignal]`, and `[WorkflowQuery]` methods.

### Don't use wall-clock time in workflows

```csharp
// WRONG — non-deterministic on replay
var utcNow = DateTime.UtcNow;
var localNow = DateTimeOffset.Now;

// CORRECT
var now = Workflow.UtcNow;
```

**Why:** Temporal replays workflow code deterministically from event history. `DateTime.UtcNow` returns a different value on each replay, causing the workflow to diverge from its recorded history and fail with a non-determinism error.

### Don't use Random or Guid.NewGuid() in workflows

```csharp
// WRONG — different value on each replay
var randomId = Guid.NewGuid();
var randomNumber = new Random().Next();

// CORRECT
var id = Workflow.NewGuid();
var n = Workflow.Random.Next();
```

**Why:** Same reason as wall-clock time — these produce different values on replay.

### Don't call ActivitySource.StartActivity() in workflow code

```csharp
// WRONG — OTel spans are non-deterministic side effects
using var span = mySource.StartActivity("my-span");

// CORRECT — agent spans are emitted in AgentActivities and DefaultTemporalAgentClient,
// both of which run outside the workflow execution context.
```

**Why:** `System.Diagnostics.Activity` creates spans with timestamps and IDs that differ on replay. All agent OTel spans (`agent.turn`, `agent.client.send`) are already emitted in the correct context — activities and client code.

### Don't query the agent registry in workflow code

```csharp
// WRONG — registry may change between original execution and replay
var names = options.GetRegisteredAgentNames();
var exists = options.IsAgentRegistered("MyAgent");

// CORRECT — wrap in an activity
var names = await Workflow.ExecuteActivityAsync(
    (RoutingActivities a) => a.GetAvailableAgents(),
    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
```

**Why:** If agents are added or removed between the original execution and a replay, the registry returns different results, causing a non-determinism error. Activity results are cached in history and replayed deterministically. See [Routing Patterns — Dynamic Routing via Activity](./routing.md#dynamic-routing-through-a-registry-activity) for the full pattern.

### Don't use `Task.WhenAll` in workflow code — use `Workflow.WhenAllAsync`

```csharp
// WRONG — not the project convention
var results = await Task.WhenAll(toolTasks);

// CORRECT — workflow-safe SDK combinator
var results = await Workflow.WhenAllAsync(toolTasks);
```

**Why:** `Workflow.WhenAllAsync` is the SDK-provided workflow-safe combinator and the project convention. Use it to await activity tasks scheduled by workflow code.

### Don't use `ConfigureAwait(false)` in workflow code

```csharp
// WRONG — opts out of TaskScheduler.Current
await Workflow.ExecuteActivityAsync(...).ConfigureAwait(false);

// CORRECT — stay on the workflow scheduler (omit, or use ConfigureAwait(true))
await Workflow.ExecuteActivityAsync(...);
await Workflow.ExecuteActivityAsync(...).ConfigureAwait(true);
```

**Why:** `ConfigureAwait(false)` opts the continuation out of the current Temporal workflow `TaskScheduler`. Subsequent workflow commands no longer execute through the active workflow context and the workflow may stop making progress. This is a `[Workflow]`-only rule — `AgentActivities.cs` and `DefaultTemporalAgentClient.cs` correctly use `ConfigureAwait(false)` because they are not workflow code. The same rule applies to the workflow paths in `DurableChatClient` and `DurableAIFunction`.

### Do invoke registered agents through activities

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Common;
using Temporalio.Workflows;

var sessionId = WorkflowAgents.NewAgentSessionId("WeatherAgent");
var requestId = Workflow.NewGuid().ToString("N");
var result = await Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "WeatherAgent", sessionId.WorkflowId, requestId, prompt),
    new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            NonRetryableErrorTypes =
            [
                nameof(DurableTurnFailedException),
                nameof(DurableTurnOutcomeTooLargeException),
            ],
        },
    });
```

Workflow code must not invoke a registered proxy directly. The activity resolves it from scoped DI;
workflow-generated session and request IDs make retries target the same logical agent turn.
Use the [bounded invocation policy](./usage.md#invoking-agents-from-a-workflow) and exclude retained terminal outcomes from the outer
activity's retries: the same request ID returns the same failure, not a fresh turn. Transport
failures, session-busy responses, and lost completions remain retryable within the budget.

---

## Agent Registration and DI

### Do use the fluent API for registration

```csharp
// GOOD
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

`AddTemporalAgents` registers the workflow, activities, keyed proxies, and `ITemporalAgentClient` in a single call.

### Don't call builder.Build() twice

```csharp
// WRONG — building twice creates separate DI containers
var host1 = builder.Build();
var host2 = builder.Build(); // throws InvalidOperationException

// CORRECT
var host = builder.Build();
await host.StartAsync();
```

### Do use DI factories on `AddDurableAgent` instead of `BuildServiceProvider()`

```csharp
opts.AddDurableAgent("MyAgent", agent =>
{
    agent.Instructions = "...";
    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
    agent.AddTool("do_thing", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<IMyService>().DoThingAsync,
        name: "do_thing"));
});
```

**Why:** every builder slot that needs a dependency takes a `Func<IServiceProvider, T>` evaluated
after the container is built, so calling `BuildServiceProvider()` inside the configure delegate is
never necessary — and doing it would fork the container, giving you a second set of singletons.

### Don't assume the builder's factory slots share a lifetime

They do not, and the difference decides where a scoped service such as a `DbContext` may safely go:

| Slot | Provider the factory receives | How often it runs |
|---|---|---|
| `agent.ChatClient` | the activity attempt's **scope** | for each model-step, preparation, or completion activity attempt |
| `agent.AddContextProvider(factory)` | the activity attempt's **scope** | preparation and completion activity attempts; a retry may resolve it again |
| `agent.AddTool(name, factory)` | the activity attempt's **scope** | when registered tools are resolved for preparation, each model step, and each tool-call activity |
| `agent.ConfigureAgentPipeline` | **none — it is not a DI factory** | for each live pipeline build, plus one dry validation build at worker startup |

`ConfigureAgentPipeline` is the odd one out: its type is `Action<AIAgentBuilder>`, so the callback
never sees an `IServiceProvider`. The attempt's scope reaches the pipeline one level down — the
library calls `builder.Build(scopedServices)`, and MAF hands that provider to any middleware factory
you registered that accepts one. Middleware you construct eagerly inside the callback gets nothing
from DI.

**Why:** the tool factory runs in activity scopes, not at worker startup. Dependencies resolved
there are scoped to that activity attempt; do not retain them in static or other long-lived state.
The function is resolved again in the tool-call activity that invokes it, so any captured scoped
service remains within that activity's scope.

Resolve per-invocation state inside the tool body instead. Capture `IServiceScopeFactory` — itself
a singleton, so capturing it is correct — and open a scope per call:

```csharp
agent.AddTool("do_thing", sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return AIFunctionFactory.Create(
        async (string id) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();
            return await db.LookupAsync(id);
        },
        name: "do_thing");
});
```

This keeps per-call dependencies inside the tool-call activity's scope.

### Use `TemporalAgentContext.Current` inside registered agent tool activities

`TemporalAgentContext.Current` (namespace `TemporalCommunity.Extensions.Agents.Session`) exposes
`GetService<T>()` against the tool activity's scope. It is established for tool calls dispatched by
the registered `AgentWorkflow`:

| Path | Context | Why |
|---|---|---|
| Managed session (`AgentWorkflow`) | available | the workflow ID is the agent session ID |

Use it for current-session access, approvals, or Temporal client operations from that activity.

---

## Session and History Management

### Do give each independent conversation its own session

The proxy examples in this section are for external callers or activity code, not workflow code.

```csharp
// External caller: keep one proxy session for each conversation.
var agent = services.GetTemporalAgentProxy("Analyst");
var s1 = await agent.CreateSessionAsync();
var s2 = await agent.CreateSessionAsync();
await agent.RunAsync("Question A", s1);
await agent.RunAsync("Question B", s2); // does not see Question A
```

**Why:** each session targets its own registered `AgentWorkflow`, so conversation history and
StateBag remain isolated.

Reuse the same session object for the turns that belong to one conversation — a fresh session per
call accumulates nothing.

### Don't run two turns over the same session at once

```csharp
// WRONG — overlapping calls on the same session object fail fast
var session = await agent.CreateSessionAsync();
await Task.WhenAll(
    agent.RunAsync("Question A", session),
    agent.RunAsync("Question B", session));

// CORRECT — parallel conversations get a session each
var a = await agent.CreateSessionAsync();
var b = await agent.CreateSessionAsync();
await Task.WhenAll(
    agent.RunAsync("Question A", a),
    agent.RunAsync("Question B", b));
```

**Why:** `TemporalAgentSession` rejects overlapping `RunAsync` calls on the same session object
with `InvalidOperationException`; it does not queue them for the workflow to serialize. Use a
distinct session for each independent conversation and sequence turns that belong to one
conversation.

### Do pass a TemporalAgentSession, not any AgentSession

The registered proxy requires a `TemporalAgentSession` so it can resolve the workflow ID and
preserve session StateBag data. Obtain sessions from `CreateSessionAsync()` or construct one with an
application-owned `TemporalAgentSessionId`.

### Do use explicit session keys for deterministic routing

```csharp
// One session per user — always routes to the same workflow
var sessionId = new TemporalAgentSessionId("MyAgent", userId);
var session = new TemporalAgentSession(sessionId);
```

### Don't serialize TemporalAgentSession directly

```csharp
// WRONG — TemporalAgentSession is not in the source-gen JSON context, so this
// silently resolves through the reflection resolver and loses the MAF history
// discriminators.
JsonSerializer.Serialize(session, DefaultOptions);

// CORRECT — the agent's own boundary writes the supported wire shape,
// including conversation history.
JsonElement serialized = await agent.SerializeSessionAsync(session);
TemporalAgentSession restored =
    (TemporalAgentSession)await agent.DeserializeSessionAsync(serialized);

// If you only need the StateBag, not the conversation:
JsonElement serializedBag = session.StateBag.Serialize();
```

**Why:** the persisted shape is `TemporalAgentSessionSnapshot`, which *is* registered in the
source-gen context. Going around it drops history and gives up the generated-metadata guarantees
the rest of the workflow wire format depends on.

If you pass your own `JsonSerializerOptions`, derive them from
`TemporalAgentJsonUtilities.DefaultOptions`. The `agent_request` / `agent_response` discriminators
are registered by a runtime resolver modifier rather than by attributes, so unrelated options
cannot round-trip history.

> **Note:** `SerializeStateBag()` is an `internal` method on `TemporalAgentSession` used by the
> framework itself (in `AgentActivities`). It is not part of the public API. User code should call
> `session.StateBag.Serialize()` directly when only StateBag persistence is required.

The registered `AgentWorkflow` carries its history and StateBag across its own Continue-as-New
transition. A regular workflow passes stable session and request IDs to its invocation activities;
it does not serialize the agent session into its own input.

---

## Activity Timeouts

### Do set appropriate timeouts for your use case

```csharp
opts.DefaultActivityTimeout = TimeSpan.FromMinutes(10); // for fast models
opts.DefaultActivityTimeout = TimeSpan.FromMinutes(20); // in-tool approval activity
opts.DefaultApprovalTimeout = TimeSpan.FromMinutes(15); // shorter than the activity timeout
opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);
```

The default (5 minutes) is reasonable for many model calls. For **in-tool** approval, configure
`ActivityTimeout` and `ApprovalTimeout` together so the activity outlives the approval wait; the
heartbeat pump remains active during that wait. Workflow-parked approval runs no tool activity while
waiting and does not need a long activity timeout. Prefer it for review windows longer than minutes;
see [HITL Patterns](./hitl-patterns.md).

### Do set HeartbeatTimeout shorter than ActivityTimeout

```csharp
// GOOD — heartbeats every 5 min, total timeout 30 min
opts.DefaultActivityTimeout    = TimeSpan.FromMinutes(30);
opts.DefaultHeartbeatTimeout   = TimeSpan.FromMinutes(5);

// BAD — heartbeat timeout longer than activity timeout defeats the purpose
opts.DefaultActivityTimeout    = TimeSpan.FromMinutes(5);
opts.DefaultHeartbeatTimeout   = TimeSpan.FromMinutes(30);
```

**Why:** `HeartbeatTimeout` detects stuck activities by checking for periodic progress signals. If it exceeds `ActivityTimeout`, the activity times out before a heartbeat check can trigger.

### Do configure the activity that invokes the registered agent

```csharp
using Temporalio.Common;
using TemporalCommunity.Extensions.AI.Exceptions;

await Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "ResearcherAgent", sessionId.WorkflowId, requestId, prompt),
    new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
            MaximumInterval = TimeSpan.FromSeconds(2),
            NonRetryableErrorTypes =
            [
                nameof(DurableTurnFailedException),
                nameof(DurableTurnOutcomeTooLargeException),
            ],
        },
    });
```

**Why:** these options govern the parent workflow's invocation activity. The registered agent's
model and tool activities use the agent worker configuration. Generate the session and request IDs
in workflow code before scheduling the invocation activity. The wrapper does not heartbeat. Tune
the [per-invocation budget](./usage.md#invoking-agents-from-a-workflow) to your deadline and SLA
while excluding retained terminal outcomes.

---

## Observability

### Do register all four ActivitySource names

```csharp
// WRONG — missing agent spans
builder.AddSource(TracingInterceptor.ClientSource.Name);

// CORRECT — all four sources
builder.AddSource(
    TracingInterceptor.ClientSource.Name,
    TracingInterceptor.WorkflowsSource.Name,
    TracingInterceptor.ActivitiesSource.Name,
    TemporalAgentTelemetry.ActivitySourceName);
```

**Why:** Each source emits a different layer of the trace hierarchy. Missing one creates gaps in your distributed traces. See [Observability](./observability.md) for the full setup.

### Do decorate the registered `IChatClient` when you need per-LLM-call visibility

The `agent.turn` span is emitted per LLM-step activity, not per whole turn — a turn with two tool rounds produces three of them. It carries Temporal-owned correlation, not provider-semantic request/response detail. To see token counts, finish reason, and the exact payloads for each round, decorate the `IChatClient` your agent factory returns:

```csharp
opts.AddDurableAgent("Assistant", agent =>
{
    agent.ChatClient = sp => new AuditingChatClient(
        sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
        sp.GetRequiredService<ILogger<AuditingChatClient>>());
});
```

**Why:** Per-LLM-call observability is a different problem from per-tool durability. Adding a logging decorator changes nothing about Temporal's checkpoint shape; it just adds round-level detail to your existing telemetry. Do not add `UseFunctionInvocation()`; the workflow owns durable tool dispatch, and a `FunctionInvokingChatClient` in that chain now fails the activity non-retryably before the model is called. See [Intercepting LLM Calls](./llm-call-interception.md) for the full guide — durable calls use `GetResponseAsync`.

### Do pre-register search attributes, or explicitly opt out

Search attribute upserts are enabled by default. Register the three attributes before starting a
production worker, or set `EnableSearchAttributes = false` to opt out:

```csharp
opts.EnableSearchAttributes = false; // opt out when the attributes are unavailable
```

When enabled, register the three attributes before starting the worker — this is required even
for a local `temporal server start-dev`; it is **not** automatic:

```bash
# Local dev server: pass the flags at startup
temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int

# Production clusters: register once via the operator CLI
temporal operator search-attribute create --name AgentName --type Keyword
temporal operator search-attribute create --name SessionCreatedAt --type Datetime
temporal operator search-attribute create --name TurnCount --type Int
```

If you leave the default enabled without pre-registering the attributes, the workflow fails with an opaque "unexpected workflow task failure".

---

## Testing

### Do use exact exception types with Assert.Throws

```csharp
// WRONG — xUnit requires exact type match
Assert.Throws<ArgumentException>(() => Foo(null));

// CORRECT
Assert.Throws<ArgumentNullException>(() => Foo(null));
Assert.Throws<ArgumentException>(() => Foo(""));
```

**Why:** xUnit's `Assert.Throws<T>` matches the **exact** type, not subtypes. `ArgumentNullException` inherits from `ArgumentException`, but `Assert.Throws<ArgumentException>` will fail if `ArgumentNullException` is thrown.

### Configure a local Temporal server for Agents integration tests

```csharp
using TemporalCommunity.Extensions.Agents;
using Temporalio.Testing;

internal static class AgentTestEnvironment
{
    internal static async Task<WorkflowEnvironment> StartLocalAsync()
    {
        var environment = await WorkflowEnvironment.StartLocalAsync(new()
        {
            DevServerOptions = new()
            {
                DownloadVersion = "v1.8.0",
                ExtraArgs =
                [
                    "--search-attribute", "AgentName=Keyword",
                    "--search-attribute", "SessionCreatedAt=Datetime",
                    "--search-attribute", "TurnCount=Int",
                ],
            },
        });
        environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        return environment;
    }
}
```

The repository's `TestEnvironmentHelper` and `TemporalServiceTestEnvironment` are test-project
helpers, not package APIs. `AgentWorkflow` upserts `AgentName` (Keyword), `SessionCreatedAt`
(Datetime), and `TurnCount` (Int) when `EnableSearchAttributes` is enabled, which is the default.
The example configures the SDK's local dev server with those attributes, pins Temporal CLI `v1.8.0`
(Temporal Service 1.31.2), and sets the required agent data converter. For an external test service,
pre-register the same attributes in its namespace, use Temporal Service 1.31.0 or later, and set
`TemporalAgentDataConverter.Instance` on the client. If a test explicitly disables
`EnableSearchAttributes`, it does not need these custom attributes.

This starts Temporal's local test server; no external Temporal deployment or Docker is required.
See [Testing Agents](./testing-agents.md) for a full fixture pattern.

### Do validate eagerly with string.IsNullOrEmpty + InvalidOperationException

```csharp
// GOOD — for configuration values
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is required.");

// LESS GOOD — ArgumentNullException implies a parameter, not a config value
if (apiKey is null) throw new ArgumentNullException(nameof(apiKey));
```

---

## Security and Configuration

### Don't treat per-run tool selection as authorization

```csharp
// Exposure control: useful, but not an authorization grant.
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["issue_refund"],
};
```

`EnableToolCalls` and `EnableToolNames` control which registered tool declarations reach the
provider and which model-returned calls the workflow may dispatch. They do not authenticate the
caller or authorize an external effect. A write tool must validate current tenant, user, resource,
and policy data inside its activity immediately before the effect. Do not put function arguments,
tool inventories, or unknown-versus-excluded distinctions in tenant-visible denial text; the
library uses one generic blocked result and limits requested-name diagnostics to operator logs.

### Don't commit real API keys in appsettings.json

```jsonc
// WRONG — checked into source control
{ "OPENAI_API_KEY": "sk-abc123..." }
```

```bash
# CORRECT — store secrets outside the repo using dotnet user-secrets
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project <path-to-project>
```

All samples load secrets from `dotnet user-secrets`, which stores values in `~/.microsoft/usersecrets/` (outside the repo) and is automatically picked up by `Host.CreateApplicationBuilder()` in the Development environment.

### Do use NuGet packages for Temporal SDK dependencies

```xml
<!-- CORRECT -->
<PackageReference Include="Temporalio" Version="1.21.1" />

<!-- WRONG — requires Rust toolchain to build from source -->
<ProjectReference Include="path/to/sdk-dotnet/src/Temporalio/Temporalio.csproj" />
```

**Why:** The Temporal .NET SDK includes a native Rust bridge (`sdk-core-c-bridge`). Project references require the Rust toolchain to compile.

---

## Tool Activity Ordering and Retries

### Do rely on the one-attempt default for effectful tools

```csharp
agents.Agent(
    "SupportAgent",
    new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions
        {
            Tools = [sendEmailTool, lookupOrderTool],
        },
    });
```

**Why:** Tool calls are separate `InvokeAgentTool` activities and execute serially in model order. Each activity gets one attempt by default. A failed write can still have an uncertain external outcome, so do not blindly retry it; use an idempotency key and reconcile with the system of record. Setting `agent.RetryPolicy` opts every tool on that agent into the same retry policy, so only do that when all of them are retry-safe. See [Durable Agents](./durable-agents.md).

With the MAF-options registration surface, set `RetryPolicy` in the per-agent Temporal options
callback only when every tool on that agent is safe to repeat. It opts all of that agent's tools into
retries; there is no
per-tool retry override on this registration path.

### Do not fan out calls from one model response

The durable-agent loop invokes one tool activity at a time, applying its result and StateBag
write-back before dispatching the next call. Keep calls in model order; do not replace this with
`Workflow.WhenAllAsync`. For independent agents, schedule one proxy-invocation activity per
agent with its own session and request ID, then await the activities with `Workflow.WhenAllAsync`.

---

## Quick Reference Table

| Rule | Category | Severity |
|------|----------|----------|
| Use `Workflow.UtcNow` not `DateTime.UtcNow` | Determinism | Fatal |
| Use `Workflow.NewGuid()` not `Guid.NewGuid()` | Determinism | Fatal |
| Don't query agent registry in workflows | Determinism | Fatal |
| Don't call `ActivitySource.StartActivity()` in workflows | Determinism | Fatal |
| Use `Workflow.WhenAllAsync`, not `Task.WhenAll`, in workflows | Determinism | Convention |
| Don't use `ConfigureAwait(false)` in `[Workflow]` code | Determinism | Workflow scheduler is lost |
| Leave tool retry unset for effectful tools | One attempt by default | Uncertain external-effect outcomes |
| Register all 4 OTel sources | Observability | Silent data loss |
| Set `ActivityTimeout` for HITL | Timeouts | Activity failure |
| Don't invoke a registered agent proxy from workflow code | Activity invocation | Workflow code must use an activity that resolves the proxy |
| Use `dotnet user-secrets` for secrets | Security | Credential leak |
| Use exact exception types in xUnit | Testing | Test failures |

---

## References

- [Durability & Determinism](../../architecture/MAF/durability-and-determinism.md) — replay guarantees and failure scenarios
- [Routing Patterns](./routing.md) — safe vs. unsafe registry access contexts
- [Observability](./observability.md) — OTel setup and span hierarchy
- [Intercepting LLM Calls](./llm-call-interception.md) — chat-client decorators via `agent.ChatClient`, and agent middleware via `ConfigureAgentPipeline`
- [Testing Agents](./testing-agents.md) — test patterns and fixtures
- [Durable Agents](./durable-agents.md) — serial tool dispatch, one-attempt default, agent-level retry opt-in, iteration cap

---
