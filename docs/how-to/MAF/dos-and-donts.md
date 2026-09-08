# Do's and Don'ts — TemporalAgents (MAF)

A consolidated reference of common mistakes and best practices when building durable agents with `TemporalCommunity.Extensions.Agents`. Each entry explains the rule, why it matters, and what to do instead.

> **Note:** Temporal workflow determinism rules (wall-clock time, `Random`, `ActivitySource`, `ConfigureAwait(false)`) apply to all Temporal .NET projects, not just MAF. Those fundamentals are documented in [CLAUDE.md](../../CLAUDE.md#workflow-best-practices) and the [Temporal SDK docs](https://docs.temporal.io/). This guide focuses on **MAF integration specifics**.

---

## Table of Contents

1. [Agent Registration and DI](#agent-registration-and-di)
2. [Session and History Management](#session-and-history-management)
3. [Workflow Sub-Agents](#workflow-sub-agents)
4. [Observability & Search Attributes](#observability--search-attributes)
5. [Testing](#testing)
6. [Per-Tool Activity Configuration](#per-tool-activity-configuration)
7. [Scheduling](#scheduling)

---

## Agent Registration and DI

### Do use the fluent API for registration

```csharp
// GOOD
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

`AddTemporalAgents` registers the workflow, activities, keyed proxies, and `ITemporalAgentClient` in a single call.

### Do use DI factories on `AddDurableAgent` for agents that need scoped services

```csharp
opts.AddDurableAgent("MyAgent", agent =>
{
    agent.Instructions = "...";
    agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
    agent.AddTool("do_thing", sp => AIFunctionFactory.Create(
        sp.GetRequiredService<IMyService>().DoThingAsync,
        "do_thing"));
});
```

The `ChatClient`, `AddTool(name, factory)`, and `AddContextProvider(factory)` builder slots accept a `Func<IServiceProvider, T>` evaluated lazily at activity dispatch. There is no need to call `BuildServiceProvider()` from inside the configure delegate.

---

## Session and History Management

### Don't reuse a TemporalAIAgent instance for independent conversations

```csharp
// WRONG — session2 sees session1's history because they share the instance
var agent = WorkflowAgents.GetTemporalAgent("Analyst");
var s1 = await agent.CreateSessionAsync();
await agent.RunAsync("Question A", s1);
var s2 = await agent.CreateSessionAsync();
await agent.RunAsync("Question B", s2); // sees "Question A" in context!

// CORRECT — separate instances have independent histories
var agent1 = WorkflowAgents.GetTemporalAgent("Analyst");
var agent2 = WorkflowAgents.GetTemporalAgent("Analyst");
var s1 = await agent1.CreateSessionAsync();
var s2 = await agent2.CreateSessionAsync();
```

**Why:** `TemporalAIAgent` stores conversation history on the instance. Two sessions on the same instance accumulate into a single history list.

### Do use explicit session keys for deterministic routing

```csharp
// One session per user — always routes to the same workflow
var sessionId = new TemporalAgentSessionId("MyAgent", userId);
var session = new TemporalAgentSession(sessionId);
```

### Don't serialize TemporalAgentSession directly

```csharp
// WRONG — TemporalAgentSession is not in the source-gen JSON context
JsonSerializer.Serialize(session, DefaultOptions);

// CORRECT — use StateBag.Serialize() directly for state persistence
var serializedBag = session.StateBag.Serialize();
```

> **Note:** `SerializeStateBag()` is an `internal` method on `TemporalAgentSession` used by the framework itself. User code should call `session.StateBag.Serialize()` directly when state persistence is required.

---

## Workflow Sub-Agents

### Do pass ActivityOptions when using GetTemporalAgent() for workflow sub-agents

```csharp
var agent = WorkflowAgents.GetTemporalAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

The global `TemporalAgentsOptions` timeouts only apply to `AgentWorkflow`-based sessions. Workflow sub-agents use their own `ActivityOptions`.

For general timeout guidance, see [CLAUDE.md Activity Timeouts](../../CLAUDE.md#critical-durability-and-determinism).

### Do use GetTemporalAgent() with string constants or activity results

```csharp
// GOOD — string literal, deterministic
var agent = WorkflowAgents.GetTemporalAgent("WeatherAgent");

// GOOD — agent name from a cached activity result
var agentName = await Workflow.ExecuteActivityAsync(
    (RoutingActivities a) => a.ValidateAgent(chosenName, "FallbackAgent"),
    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
var agent = WorkflowAgents.GetTemporalAgent(agentName);
```

### Do query the agent registry only inside activities, not workflow code

```csharp
// WRONG — registry may change between original execution and replay
var names = options.GetRegisteredAgentNames();

// CORRECT — wrap in an activity (result is cached in history)
var names = await Workflow.ExecuteActivityAsync(
    (RoutingActivities a) => a.GetAvailableAgents(),
    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
```

**Why:** If agents are added or removed between the original execution and a replay, the registry returns different results, causing a non-determinism error. Activity results are cached in history and replayed deterministically. See [Routing Patterns — Dynamic Routing via Activity](./routing.md#pattern-2-dynamic-routing-via-activity) for the full pattern.

---

## Observability & Search Attributes

### Do pre-register search attributes, or explicitly opt out

Search attribute upserts are enabled by default. Register the three attributes before starting a production worker, or set `EnableSearchAttributes = false` to opt out:

```csharp
opts.EnableSearchAttributes = false; // opt out when the attributes are unavailable
```

When enabled, register the three attributes before starting the worker — this is required even for a local `temporal server start-dev`; it is **not** automatic:

```bash
# Local dev server: pass the flags at startup
temporal server start-dev --namespace default \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int

# Production clusters: register once via the operator CLI
temporal operator search-attribute create --name AgentName --type Keyword
temporal operator search-attribute create --name SessionCreatedAt --type Datetime
temporal operator search-attribute create --name TurnCount --type Int
```

If you leave the default enabled without pre-registering the attributes, the workflow fails with an opaque "unexpected workflow task failure".

### Do decorate the registered `IChatClient` when you need per-LLM-call visibility

The `agent.turn` span captures one whole turn (LLM call plus all tool rounds). If you need to see each individual LLM request/response — token counts per round, finish reason per round, the exact tool-call payloads — decorate the registered `IChatClient` before the durable agent resolves it:

```csharp
builder.Services.AddSingleton<IChatClient>(
    new LoggingChatClient(innerChatClient, logger));

opts.AddDurableAgent("Assistant", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
});
```

**Why:** Per-LLM-call observability is a different problem from per-tool durability. Adding a logging decorator changes nothing about Temporal's checkpoint shape; it just adds round-level detail to your existing telemetry. Do not add `UseFunctionInvocation()`; the workflow owns durable tool dispatch. See [LLM-Call Interception](./llm-call-interception.md) for the full guide.

---

## Testing

### Do use TestEnvironmentHelper.StartLocalAsync() for Agents integration tests

```csharp
// TemporalCommunity.Extensions.Agents integration tests
var env = await TestEnvironmentHelper.StartLocalAsync();
```

`AgentWorkflow` calls `UpsertTypedSearchAttributes` only when `EnableSearchAttributes = true`. If search attributes are enabled in your test fixture, the three custom attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) must be pre-registered when the embedded server starts — otherwise the workflow fails with an opaque "unexpected workflow task failure". `TestEnvironmentHelper.StartLocalAsync()` passes the required `--search-attribute` CLI args automatically.

Because `EnableSearchAttributes` defaults to `true`, use `TestEnvironmentHelper` for standard Agents integration tests. Bare `WorkflowEnvironment.StartLocalAsync()` is sufficient only when the test explicitly disables search attributes. For `TemporalCommunity.Extensions.AI` integration tests (which use `DurableChatWorkflow` and never require custom search attributes):

```csharp
// TemporalCommunity.Extensions.AI integration tests only
var env = await WorkflowEnvironment.StartLocalAsync();
```

See [Testing Agents](./testing-agents.md) for the full fixture pattern.

---

## Per-Tool Activity Configuration

### Do call `opts.NoRetry()` on write-style tools

```csharp
opts.AddDurableAgent("SupportAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

    // Write tool — non-idempotent, must not double-fire on retry.
    agent.AddTool(sendEmailTool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

    // Read tool — idempotent, inherits worker default retry policy.
    agent.AddTool(lookupOrderTool);
});
```

**Why:** Every tool in a durable agent is dispatched as a separate Temporal activity (`InvokeAgentTool`). A transient activity failure normally retries — for a non-idempotent write tool that already had a side effect, the retry would fire the side effect a second time. `opts.NoRetry()` is sugar for `RetryPolicy = new() { MaximumAttempts = 1 }`, which tells Temporal not to re-execute the activity. See [Durable Agents](./durable-agents.md).

### Do use `Workflow.WhenAllAsync` for parallel activity fan-out

```csharp
var tasks = toolCalls.Select(tc =>
    Workflow.ExecuteActivityAsync(
        (AgentActivities a) => a.InvokeAgentToolAsync(BuildInput(tc)),
        ResolveToolActivityOptions(tc.Name))).ToList();

var results = await Workflow.WhenAllAsync(tasks);
```

**Why:** `Workflow.WhenAllAsync` is the workflow-safe combinator and preserves input order — `results[i]` corresponds to `tasks[i]`, which lets you correlate fan-out activity results to the requests that produced them. The durable-agent loop in `AgentWorkflow` uses exactly this pattern.

---

## Scheduling

### Do delete schedules before decommissioning agents

```csharp
var handle = client.GetAgentScheduleHandle("daily-summary");
await handle.DeleteAsync();
```

**Why:** Temporal Schedules are independent of workers. Removing an agent from `TemporalAgentsOptions` does **not** delete its schedule — it continues firing and fails with `AgentNotRegisteredException`.

### Don't assume config-time schedule changes take effect on restart

```csharp
// This change is SILENTLY SKIPPED if the schedule already exists
opts.AddScheduledAgentRun("Agent", "my-schedule", request, updatedSpec);
```

**Why:** `ScheduleRegistrationService` catches `ScheduleAlreadyRunningException` and logs a warning. To apply updated specs, delete the schedule first. See [Scheduling](./scheduling.md#pitfalls-and-gotchas) for details.

---

## Quick Reference Table

| Rule | Category | Impact |
|------|----------|--------|
| Use `GetTemporalAgent()` with constants or activity results | Workflow safety | Determinism error on registry change |
| Don't reuse `TemporalAIAgent` instances | Sessions | Unintended history mixing |
| Pass `ActivityOptions` to `GetTemporalAgent()` sub-agents | Sub-agents | Activity timeout failure |
| Pass `opts => opts.NoRetry()` to `agent.AddTool` for write tools | Per-tool retry | Non-idempotent re-execution |
| Pre-register search attributes or opt out | Observability | "Unexpected workflow task failure" |
| Use `TestEnvironmentHelper.StartLocalAsync()` for agent tests | Testing | Search attribute registration failure |
| Delete schedules before removing agents | Scheduling | Orphaned schedules firing forever |
| Don't assume schedule config changes take effect | Scheduling | Silent misconfig |

---

## References

- [CLAUDE.md — Workflow Best Practices](../../CLAUDE.md#workflow-best-practices) — Temporal determinism rules, ActivitySource, timeouts, ConfigureAwait
- [Durability & Determinism](../../architecture/MAF/durability-and-determinism.md) — replay guarantees and failure scenarios
- [Routing Patterns](./routing.md) — safe vs. unsafe registry access contexts
- [Durable Agents](./durable-agents.md) — per-tool retry pattern, `opts.NoRetry()` sugar
- [Scheduling](./scheduling.md) — schedule lifecycle and pitfalls
- [LLM-Call Interception](./llm-call-interception.md) — per-LLM-call decorators
- [Testing Agents](./testing-agents.md) — test patterns and fixtures
- [Temporal .NET SDK Docs](https://docs.temporal.io/develop/dotnet) — determinism, workflow rules, activity configuration

---

_Last updated: 2026-09-07_
