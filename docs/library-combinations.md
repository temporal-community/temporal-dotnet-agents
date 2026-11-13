# Library Combinations Guide

Choose the integration that matches the AI abstraction in your application:
`IChatClient` apps use `TemporalCommunity.Extensions.AI`, while Microsoft Agent Framework
(`AIAgent`) apps use `TemporalCommunity.Extensions.Agents`. Both make AI work durable with
Temporal, but each has its own registration, session, and tool contracts.

If you're unsure, start with the stack your app already uses. Avoid combining MAF with
`TemporalCommunity.Extensions.AI`; use `TemporalCommunity.Extensions.Agents` for MAF. Both
integrations require Temporal Service 1.31.0 or newer.

---

## Choose Your Path

Use this page as the canonical package-selection guide. After choosing a package, select a runnable
project from the [Sample Catalog](../samples/catalog.md); do not copy registration, history, or tool
assumptions from the other package's samples.

### The Two Combinations at a Glance

| | Combination 1 | Combination 2 |
|---|---|---|
| **Stack** | MEAI + `Extensions.AI` | MAF + `Extensions.Agents` |
| **Entry point** | `DurableChatSessionClient` | `ITemporalAgentClient` / `TemporalAIAgentProxy` |
| **Registration** | `AddDurableAI()` | `AddTemporalAgents(...).Agent(...)` |
| **NuGet package** | `TemporalCommunity.Extensions.AI` | `TemporalCommunity.Extensions.Agents` |
| **Named agents** | No | Yes |
| **Temporal UI search attributes** | No | Yes — `AgentName`, `SessionCreatedAt`, `TurnCount` (enabled by default) |
| **StateBag / AIContextProvider** | No | Yes |
| **HITL** | Yes | Yes |
| **Embeddings** | Yes | Yes — inject `IEmbeddingGenerator` into tool classes via DI |

---

## Combination 1 — MEAI + `TemporalCommunity.Extensions.AI`

**The designed happy path for `TemporalCommunity.Extensions.AI`.**

`DurableChatWorkflow` wraps an `IChatClient` directly. Every turn becomes a Temporal activity; every conversation becomes a workflow identified by a `conversationId` string you control. No Microsoft Agent Framework is required.

### Registration

```csharp
// Worker + client in the same host (common pattern)
builder.Services.AddChatClient(innerClient);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    })
    .AddDurableTool(weatherTool); // one implicit worker-owned default toolset
```

`AddDurableAI()` is the concise co-hosted registration. When the session client and worker run in
separate processes, use `AddDurableAIClient(taskQueue)` with `AddTemporalClient(...)` on the client
and `AddDurableAIWorker()` with `AddHostedTemporalWorker(...)` on the worker. The client process
needs neither a provider client nor tool implementations; those remain worker-side.

### Usage

```csharp
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.SendAsync(
    "conv-user-42",
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);
```

### What you get

- **Managed conversations** — `DurableChatSessionClient` starts workflows that own conversation
  history and continue-as-new behavior.
- **Worker-owned tools** — register an implicit tool group or named `AddDurableToolset(...)`
  groups; each invocation runs as its own activity. Split clients need no provider or tool
  implementations. See [Tool Functions](how-to/MEAI/tool-functions.md).
- **Custom durable turns** — build a custom workflow with
  `DurableChatWorkflowBase<TOutput>` or `DurableToolWorkflowBase<TRequestData, TTurnState>` to
  control output or turn state while retaining package-owned execution. See
  [Custom Workflow Output](how-to/MEAI/custom-workflow-output.md).
- **Direct adapters and extensions** — use application-owned activities for embeddings, optional
  model/tool queue routing, or ordinary MCP tools. See [Embeddings](how-to/MEAI/embeddings.md),
  [Operations](how-to/MEAI/operations.md), and the [MCP guides](how-to/MEAI/mcp-tools.md)
  and [workflow-backed MCP server guide](how-to/MCP/workflow-tool-server.md).

The `AddDurableAI()` registration configures the default client to use `DurableAIDataConverter`;
`AddTemporalClient()` alone does not. The converter applies to every workflow sharing that client.
See [shared-worker guidance](how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows) for
custom-client requirements.

### Limitations

Conversations are identified by an opaque string ID. There are no named agents, no Temporal UI search attributes, no `StateBag`, no `AIContextProvider` support, and no parallel orchestration primitives. If your use case needs any of these, move to Combination 2.

---

## Combination 2 — MAF + `TemporalCommunity.Extensions.Agents`

**The designed happy path for `TemporalCommunity.Extensions.Agents`.**

`AgentActivities` wraps an `AIAgent` (from `Microsoft.Agents.AI`) with a full session — structured history, `AgentSessionStateBag`, `AIContextProvider` runs, and agent-semantic OTel spans. Each agent gets its own `AgentWorkflow` instance, identified by name and a session key. By default, `EnableSearchAttributes` adds `AgentName`, `SessionCreatedAt`, and `TurnCount` search attributes that make the Temporal Web UI genuinely useful.

`TemporalCommunity.Extensions.Agents` depends on `TemporalCommunity.Extensions.AI` — installing the Agents NuGet package pulls in the AI package automatically.

### Registration

```csharp
builder.Services.AddChatClient(chatClient);

builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(agents =>
        agents.Agent(
            "WeatherAgent",
            new ChatClientAgentOptions
            {
                Description = "Handles weather queries and forecasts.",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are a weather specialist.",
                },
            },
            temporal => temporal.TimeToLive = TimeSpan.FromHours(24)));
```

Use `Agent` for the standard `ChatClientAgentOptions` path. `AddDurableAgent` exposes advanced
builder configuration, including per-tool timeouts, scoped context-provider factories, and
per-agent pipeline or tool-interceptor setup. See the
[MAF registration guidance](how-to/MAF/usage.md#registration) for the differences and a complete
comparison.

For a separate MAF caller process, register `AddTemporalAgentProxies(...)` and `AddAgentProxy(...)`
there instead of hosting the agent implementation. See [MAF usage](how-to/MAF/usage.md#quick-one-shot-call)
for both topologies.

### External caller usage

```csharp
// Resolved from DI — one proxy per registered agent name
var proxy = services.GetTemporalAgentProxy("WeatherAgent");

var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("Will it rain in Boston tomorrow?", session);
```

### Workflow orchestration usage

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var session = WorkflowAgents.NewAgentSessionId("ResearchAgent");
        return await Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                "ResearchAgent",
                session.WorkflowId,
                Workflow.NewGuid().ToString("N"),
                $"Research: {topic}"),
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
    }
}
```

The activity resolves the registered proxy and invokes it with the supplied session and request
IDs. The outer activity excludes retained terminal outcomes because a retry with the same request
ID returns the same failure; transport failures, session-busy responses, and lost completions
remain retryable within the budget. See [the bounded invocation retry contract](./how-to/MAF/usage.md#invoking-agents-from-a-workflow)
and the [workflow orchestration sample](../samples/MAF/WorkflowOrchestration/README.md).

### What you get

- **Named agent sessions** — each registered `AIAgent` runs in its own `AgentWorkflow`, which owns
  conversation history and `AgentSessionStateBag`. Ordered `AIContextProvider`s run as part of
  durable turns; see [Context Providers](how-to/MAF/context-providers.md).
- **Durable model and tool execution** — model steps and serial tool calls run as Temporal
  activities. Tools use one attempt by default; an agent retry policy affects every tool on that
  agent. See [Durable Agents](how-to/MAF/durable-agents.md).
- **MAF interaction features** — use workflow-parked approvals and typed structured responses.
  See [HITL Patterns](how-to/MAF/hitl-patterns.md) and
  [Structured Output](how-to/MAF/structured-output.md).
- **Temporal search attributes** — enabled by default for agent name, session creation time, and
  turn count, provided those attributes are registered in the namespace.

### Limitations

- Requires `Microsoft.Agents.AI`.
- Search attribute upserts are enabled by default. `AgentName`, `SessionCreatedAt`, and `TurnCount` must be pre-registered before the worker starts. Start a local dev server with `--search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`; production clusters require the equivalent one-time CLI commands. Set `EnableSearchAttributes = false` to opt out. Standard Agents integration tests should use `TestEnvironmentHelper.StartLocalAsync()` to handle pre-registration.
- Regular workflows invoke registered agents through activities that resolve
  `TemporalAIAgentProxy`; workflow code must not invoke agent proxies directly.
- Custom `[Workflow]` classes must follow Temporal determinism rules (`Workflow.UtcNow`, `Workflow.NewGuid()`, no `ActivitySource.StartActivity()` inside workflow code).

---

## Anti-pattern: MAF + `TemporalCommunity.Extensions.AI`

Do not register an `AIAgent` or `ChatClientAgent` with `AddDurableAI()`. `DurableChatWorkflow` does not know about `AIAgent`, `AgentSession`, `AgentSessionStateBag`, or `TemporalAgentContext` — the agent runs as a plain `IChatClient`. You pay the `Microsoft.Agents.AI` dependency cost and receive exactly Combination 1's capabilities, with none of the Agents-specific features.

The Agents package uses its own MAF activity/agent pipeline. It does not compose or depend on the
MEAI durable-chat transport/provider sanitizer used by `DurableChatClient` and
`DurableChatSessionClient`.

If you use `Microsoft.Agents.AI`, use Combination 2 (`AddTemporalAgents()`).

---

## Adopting Extensions.AI Incrementally

Some projects build a `[WorkflowUpdate]`-based request/response loop with `WaitConditionAsync`
before encountering these libraries. They can adopt `TemporalCommunity.Extensions.AI` selectively
rather than wholesale. For managed tools, move the functions to `AddDurableTools()` rather than
an inline `UseFunctionInvocation()` loop.

### Replace a hand-rolled chat workflow

Register the managed session worker and resolve its client from DI. `IChatClient` and tool
implementations are worker-side; callers submit turns through `DurableChatSessionClient`.
`AddTemporalClient()` registers the SDK client, and `AddDurableAI()` configures its default
converter for durable AI payloads; `AddTemporalClient()` alone does not install that converter.

```csharp
// Application-owned: HostApplicationBuilder builder and innerChatClient.
builder.Services.AddChatClient(innerChatClient);
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddHostedTemporalWorker("durable-chat").AddDurableAI();

using var host = builder.Build();
await host.StartAsync();
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
var response = await sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "Summarize my latest order.")]);
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/LibraryCombinations/AdoptingManagedSession.cs. -->

### Adopt components in a custom workflow

Keep the custom workflow and register the client and worker with the Temporal SDK hosting
extensions. In this example, the workflow exchanges only `string` and `float[]`; MEAI embedding
types stay inside the application-owned activity, so no durable AI converter is needed. If a custom
workflow exchanges MEAI payloads, configure a compatible converter explicitly:
`AddTemporalClient()` alone does not install `DurableAIDataConverter`. See the
[durable embeddings guide](how-to/MEAI/embeddings.md) for activity timeout, retry, and payload
guidance.

```csharp
// Application-owned: HostApplicationBuilder builder, taskQueue, and generator.
// EmbeddingWorkflow and EmbeddingActivities are application-defined types.
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddWorkflow<EmbeddingWorkflow>()
    .AddSingletonActivities<EmbeddingActivities>();
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/LibraryCombinations/AdoptingComponents.cs. -->

---

## Further Reading

- [Getting Started — `TemporalCommunity.Extensions.AI`](how-to/MEAI/usage.md)
- [Usage Guide — `TemporalCommunity.Extensions.Agents`](how-to/MAF/usage.md)
- [Tool Functions](how-to/MEAI/tool-functions.md) — direct durable calls, managed sessions, and worker-owned toolsets
- [Human-in-the-Loop Patterns (MEAI)](how-to/MEAI/hitl-patterns.md)
- [Human-in-the-Loop Patterns (MAF)](how-to/MAF/hitl-patterns.md)
- [Cross-Library Integration Architecture](architecture/MEAI/cross-library-integration.md)
- [Durable Chat Pipeline Architecture](architecture/MEAI/durable-chat-pipeline.md)
- [Direct-Adapter-in-Workflow Anti-Pattern](architecture/MEAI/direct-adapter-anti-pattern.md) — why constructing a durable chat adapter directly inside workflow code is retired, and what to use instead
- [Durability and Determinism](architecture/MAF/durability-and-determinism.md)
