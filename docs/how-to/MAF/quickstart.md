# MAF Quickstart

One durable agent, registered and called, in a single file.

`TemporalCommunity.Extensions.Agents` makes a Microsoft Agent Framework `AIAgent` durable with
Temporal: every LLM call is its own activity, every tool call is its own activity, and a crashed
worker resumes from event history instead of starting the conversation over.

Once this works, [usage.md](./usage.md) is the reference for everything else.

---

## Before you start

- A Temporal Service on `localhost:7233` — `temporal server start-dev`
- An `OPENAI_API_KEY`, via `dotnet user-secrets` or the environment
- The package:

```bash
dotnet add package TemporalCommunity.Extensions.Agents
```

Search attributes are pre-registered by default (`EnableSearchAttributes`), and the dev server does
**not** create them for you. Start it with them, or agent workflows fail to start:

```bash
temporal server start-dev \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int
```

---

## 1. Register the agent on a worker

`AddDurableAgent` is the only worker-hosted definition path. Tools, chat client, and instructions
all hang off one builder, and dependencies resolve through per-slot factories — no
`BuildServiceProvider()` bootstrap.

```csharp
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

var builder = Host.CreateApplicationBuilder(args);

var apiKey = builder.Configuration["OPENAI_API_KEY"]
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));

// The tool the agent may call. Each invocation the model requests becomes its own
// InvokeAgentTool activity.
static string GetWeather(string city) => $"It is sunny in {city}.";
var weatherTool = AIFunctionFactory.Create(
    GetWeather,
    name: "get_weather",
    description: "Returns the current weather for a city.");

builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(weatherTool);
        });
    })
    .AddWorkflow<AskWorkflow>();   // only needed for step 3; see below

var host = builder.Build();
await host.StartAsync();
```

Three things that bite people here:

- **Register `ITemporalClient` explicitly.** The three-argument
  `AddHostedTemporalWorker(address, namespace, queue)` overload configures a worker-internal client
  but does not put `ITemporalClient` in DI, and `AddTemporalAgents` needs one.
- **Do not call `.UseFunctionInvocation()`** on your chat client. The workflow owns tool dispatch;
  an in-process loop is rejected at runtime.
- **A tool that needs a service from DI takes the name first.** `AddTool` has two overloads: the
  one above, `AddTool(AIFunction tool, ...)`, for a tool you already built, and
  `AddTool(string name, Func<IServiceProvider, AIFunction> factory, ...)` for one that must resolve
  dependencies — for example `agent.AddTool("get_weather", sp =>
  AIFunctionFactory.Create(sp.GetRequiredService<WeatherService>().GetWeather, name:
  "get_weather"))`. There is no factory-only overload; passing a bare `sp => ...` lambda does not
  compile.

## 2. Call it from outside a workflow

The worker is running in the same process, so the host can talk to its own agent:

```csharp
var proxy   = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();

var reply = await proxy.RunAsync("What's the weather in Kingston?", session);
Console.WriteLine(reply.Text);
```

Reuse that `session` for the rest of the conversation — it is what accumulates history. A second
independent conversation gets its own `CreateSessionAsync()`.

## 3. Or call it from inside a workflow

Sub-agent calls use `WorkflowAgents.GetTemporalAgent`, which is workflow-context only — the proxy
above is the external equivalent.

```csharp
[Workflow]
public class AskWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string question)
    {
        var agent   = WorkflowAgents.GetTemporalAgent("Assistant");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);
        var reply   = await agent.RunAsync([new ChatMessage(ChatRole.User, question)], session)
            .ConfigureAwait(true);

        return reply.Messages[^1].Text ?? string.Empty;
    }
}
```

A workflow only runs if the worker knows about it, which is what the `.AddWorkflow<AskWorkflow>()`
in step 1 is for. It has to go there, chained onto the same worker builder, for two reasons: the
service collection is closed once `builder.Build()` runs, and `AddTemporalAgents` throws if called
a second time on the same builder rather than silently replacing your agent registrations.

Start it like any other workflow:

```csharp
var result = await host.Services.GetRequiredService<ITemporalClient>().ExecuteWorkflowAsync(
    (AskWorkflow wf) => wf.RunAsync("What's the weather in Kingston?"),
    new WorkflowOptions(id: $"ask-{Guid.NewGuid():N}", taskQueue: "agents"));

Console.WriteLine(result);
```

---

## Rules worth knowing on day one

- **Write tools must opt out of retry.** `agent.AddTool(tool, opts => opts.NoRetry())` — otherwise a
  transient failure re-executes a non-idempotent action.
- **One run at a time per session.** Overlapping `RunAsync` calls on the same session throw.
  Parallel conversations get a session each.
- **A session belongs to its agent.** Passing one agent's session to another is rejected.

## Where to go next

| Need | Guide |
|---|---|
| Full builder and options reference | [usage.md](./usage.md) |
| Tool retries, timeouts, write safety | [durable-agents.md](./durable-agents.md) |
| Log or trace model calls | [llm-call-interception.md](./llm-call-interception.md) |
| Approval gates and human review | [hitl-patterns.md](./hitl-patterns.md) |
| Common mistakes | [dos-and-donts.md](./dos-and-donts.md) |
| A runnable starting point | `samples/MAF/BasicAgent` |
