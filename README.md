# Temporalio.Extensions.Agents

A [Temporal](https://temporal.io/) integration for the [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). This library provides durable, stateful AI agent sessions backed by Temporal workflows, replacing the DurableTask runtime with Temporal's native capabilities.

## Why Temporal?

| Feature | DurableTask | Temporal |
|---|---|---|
| Request/Response | Signal + poll loop | `[WorkflowUpdate]` — direct response, no polling |
| Long sessions | Automatic replay | Continue-as-new transfers history to a fresh run |
| Observability | Limited | Full Temporal Web UI, event history, tracing |
| Multi-agent | Manual orchestration | First-class workflow orchestration |

## How It Works

Each agent session maps to a long-lived Temporal **workflow** (`AgentWorkflow`). When an external caller sends a message, it uses a Temporal **Update** — a durable, acknowledged request/response primitive — to deliver the message and receive the agent's response in a single call. All AI inference runs inside Temporal **activities**, preserving determinism.

```
External Caller
    │
    │  ExecuteUpdateAsync (RunRequest)
    ▼
AgentWorkflow (long-lived workflow)
    │
    │  ExecuteActivityAsync
    ▼
AgentActivities.ExecuteAgentAsync
    │
    └─► Real AIAgent (e.g., ChatClientAgent backed by Azure OpenAI)
```

## Setup

### 1. Register Temporal Agents

In your `Program.cs`, configure agents alongside your Temporal client:

```csharp
using Microsoft.Agents.AI;
using Temporalio.Extensions.Agents;

var builder = WebApplication.CreateBuilder(args);

// Build a real AIAgent (e.g., backed by Azure OpenAI via Microsoft.Agents.AI)
var chatAgent = new ChatClientAgent(chatClient, "MyAgent")
{
    Instructions = "You are a helpful assistant."
};

builder.Services.ConfigureTemporalAgents(
    configure: options =>
    {
        options.AddAIAgent(chatAgent, timeToLive: TimeSpan.FromHours(24));
    },
    taskQueue: "agents",
    targetHost: "localhost:7233",   // omit if you register ITemporalClient yourself
    @namespace: "default");
```

`ConfigureTemporalAgents` registers:
- An `ITemporalClient` (when `targetHost` is provided)
- An `ITemporalAgentClient` for sending messages to agent workflows
- A hosted Temporal worker running `AgentWorkflow` + `AgentActivities`
- A keyed `AIAgent` proxy singleton for each registered agent

### 2. Access the Agent Proxy

Inject the proxy by name from DI, or resolve it from the service provider:

```csharp
// Option A — resolve from IServiceProvider
AIAgent proxy = services.GetTemporalAgentProxy("MyAgent");

// Option B — inject with a keyed service
public class MyController(
    [FromKeyedServices("MyAgent")] AIAgent agentProxy)
{ ... }
```

## Usage Examples

### Sending a Message (External Caller)

```csharp
// Create (or resume) a session
AgentSession session = await agentProxy.CreateSessionAsync();

// Send a message and get a response
AgentResponse response = await agentProxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Messages[0].Text);
```

The session ID encodes the agent name and a unique key as a Temporal workflow ID (`ta-myagent-{key}`). Passing the same session across calls routes all messages to the same `AgentWorkflow` instance, preserving conversation history.

### Multi-Turn Conversation

```csharp
var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("What is the capital of France?", session);
Console.WriteLine(r1.Messages[0].Text);  // Paris

var r2 = await agentProxy.RunAsync("What is its population?", session);
Console.WriteLine(r2.Messages[0].Text);  // ~2.1 million (context preserved)
```

### Fire-and-Forget

For notifications or background tasks where you don't need to wait for the agent's response:

```csharp
var options = new TemporalAgentRunOptions { IsFireAndForget = true };
await agentProxy.RunAsync("Process this in the background.", session, options);
// Returns immediately with an empty AgentResponse
```

### Structured Output

Request a JSON response conforming to a specific schema:

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var session = await agentProxy.CreateSessionAsync();
var response = await agentProxy.RunAsync("What's the weather in Seattle?", session, options);
var report = response.Messages[0].GetContent<WeatherReport>();
```

### Tool Filtering

Restrict which tools the agent may use for a specific request:

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather", "search_web"],
    // EnableToolCalls = false  // disable all tools for this request
};

var response = await agentProxy.RunAsync("Look up the latest news.", session, options);
```

## Agent Orchestration (Inside Temporal Workflows)

Use `TemporalWorkflowExtensions.GetAgent` to interact with agents from within an orchestrating Temporal workflow. The agent's conversation history is stored in the workflow's event history and replayed automatically.

```csharp
using Temporalio.Workflows;
using Temporalio.Extensions.Agents;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        // Get a TemporalAIAgent — runs inference via activity, history tracked in workflow state
        var researcher = TemporalWorkflowExtensions.GetAgent("ResearcherAgent");
        var session = await researcher.CreateSessionAsync();

        var outline = await researcher.RunAsync($"Create an outline about: {topic}", session);

        var writer = TemporalWorkflowExtensions.GetAgent("WriterAgent");
        var writerSession = await writer.CreateSessionAsync();

        var draft = await writer.RunAsync(
            $"Write a short article based on this outline:\n{outline.Messages[0].Text}",
            writerSession);

        return draft.Messages[0].Text;
    }
}
```

`TemporalAIAgent` (returned by `GetAgent`) stores the conversation history as workflow state. This means it survives worker restarts, supports retries, and is durable by design — all without any extra persistence code.

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

## Session TTL

Sessions expire after the configured TTL (default: 14 days). Configure per-agent overrides:

```csharp
options.AddAIAgentFactory(
    name: "ShortLivedAgent",
    factory: sp => sp.GetRequiredService<MyCustomAgent>(),
    timeToLive: TimeSpan.FromHours(1));

// Or configure the default for all agents
options.DefaultTimeToLive = TimeSpan.FromDays(7);
```

When the TTL elapses, the `AgentWorkflow` completes naturally. The next message to that session ID starts a fresh workflow run.

## Activity Timeouts

Every agent turn — one call to `RunAsync` — executes inside a Temporal activity. Two timeouts govern that activity:

| Option | Default | What it limits |
|---|---|---|
| `ActivityStartToCloseTimeout` | 30 minutes | Total wall-clock time for one turn, including tool calls and retries |
| `ActivityHeartbeatTimeout` | 5 minutes | Maximum gap between heartbeats; Temporal retries the activity if exceeded (most relevant when streaming) |

Both are nullable `TimeSpan?` on `TemporalAgentsOptions`. When `null`, the workflow falls back to the defaults shown above.

```csharp
builder.Services.ConfigureTemporalAgents(
    configure: options =>
    {
        options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1));

        // Increase for slow models or long tool-call chains
        options.ActivityStartToCloseTimeout = TimeSpan.FromMinutes(10);

        // Increase if streaming heartbeats arrive slowly
        options.ActivityHeartbeatTimeout = TimeSpan.FromMinutes(2);
    },
    taskQueue: "agents",
    targetHost: "localhost:7233");
```

### Activity Timeouts for In-Workflow Agents

When using `TemporalWorkflowExtensions.GetAgent` inside an orchestrating workflow, pass `ActivityOptions` directly at the call site:

```csharp
var researcher = TemporalWorkflowExtensions.GetAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

## Accessing Temporal from Agent Tools

Agent tools executing inside `AgentActivities.ExecuteAgentAsync` can access Temporal capabilities through `TemporalAgentContext.Current`:

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

## Streaming Responses

Register an `IAgentResponseHandler` to stream agent responses as they are generated (e.g., for server-sent events):

```csharp
builder.Services.AddSingleton<IAgentResponseHandler, MyStreamingHandler>();

public class MyStreamingHandler : IAgentResponseHandler
{
    public async ValueTask OnStreamingResponseUpdateAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        CancellationToken ct)
    {
        await foreach (var update in stream.WithCancellation(ct))
        {
            // Push each chunk to the client (e.g., via SignalR or SSE)
        }
    }

    public ValueTask OnAgentResponseAsync(AgentResponse message, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

## Component Map (vs. DurableTask)

| DurableTask | Temporal |
|---|---|
| `AgentEntity` | `AgentWorkflow` — long-lived workflow with `[WorkflowUpdate]` |
| `DurableAIAgent` | `TemporalAIAgent` — for use inside Temporal workflows |
| `DurableAIAgentProxy` | `TemporalAIAgentProxy` — for external callers |
| `DurableAgentContext` | `TemporalAgentContext` — async-local context for tools |
| `IDurableAgentClient` + polling | `ITemporalAgentClient` + Update (no polling) |
| `AgentSessionId` | `TemporalAgentSessionId` — encodes agent name + key |
| `ConfigureDurableAgents(...)` | `ConfigureTemporalAgents(...)` — same shape |
