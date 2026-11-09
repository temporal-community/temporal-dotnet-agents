# MAF Quickstart

One durable agent, registered and called, in a single file.

`TemporalCommunity.Extensions.Agents` makes a Microsoft Agent Framework `AIAgent` durable with
Temporal: every LLM call is its own activity, every tool call is its own activity, and a crashed
worker resumes from event history instead of starting the conversation over.

Once this works, [usage.md](./usage.md) is the reference for everything else.

---

## Before you start

- .NET 10 SDK and a Temporal Service on `localhost:7233` (start command below)
- An `OPENAI_API_KEY` environment variable
- A console application with the library, provider adapter, and Generic Host packages:

```bash
dotnet new console -n DurableWeather --framework net10.0
cd DurableWeather
dotnet add package TemporalCommunity.Extensions.Agents
dotnet add package Microsoft.Extensions.AI.OpenAI
dotnet add package Microsoft.Extensions.Hosting
```

The library upserts search attributes by default (`EnableSearchAttributes`); the dev server does
**not** register their definitions for you. In a separate terminal, start it with them:

```bash
temporal server start-dev \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int
```

---

## 1. Register the agent on a worker

Configure MAF behavior once in `ChatClientAgentOptions`; keep workflow and activity settings in
`TemporalAgentOptions`. The chat client resolves from the activity's scoped service provider, and
durable tools receive that same scope through `AIFunctionArguments.Services`.

```csharp
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;

var builder = Host.CreateApplicationBuilder(args);

var apiKey = builder.Configuration["OPENAI_API_KEY"]
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));

// Each model-requested invocation becomes its own durable tool activity. Resolve scoped
// dependencies through the IServiceProvider parameter rather than capturing them here.
static string GetWeather(string city, IServiceProvider services) =>
    services.GetRequiredService<WeatherService>().GetForecast(city);
var weatherTool = AIFunctionFactory.Create(
    GetWeather,
    name: "get_weather",
    description: "Returns the current weather for a city.");

builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());
builder.Services.AddScoped<WeatherService>();
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(agents =>
        agents.Agent(
            "Assistant",
            new ChatClientAgentOptions
            {
                Description = "A helpful weather assistant.",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are a helpful assistant. Use get_weather for forecasts.",
                    Tools = [weatherTool],
                },
            },
            temporal => temporal.TimeToLive = TimeSpan.FromHours(1)));

var host = builder.Build();
await host.StartAsync();
```

`AddTemporalAgents` still requires an explicitly registered `ITemporalClient`; the hosted-worker
overload that takes address, namespace, and queue does not add one to DI. Do not call
`.UseFunctionInvocation()` on the chat client: the Temporal workflow owns the tool loop. The
`WeatherService.GetForecast(string city)` implementation is application code; the tool resolves it
through the invocation-scoped service provider.

## 2. Call it through the keyed MAF proxy

The worker and caller can share a process. Resolve the proxy as a keyed MAF `AIAgent`:

```csharp
var agent   = host.Services.GetRequiredKeyedService<AIAgent>("Assistant");
var session = await agent.CreateSessionAsync();

var reply = await agent.RunAsync("What's the weather in Kingston?", session);
Console.WriteLine(reply.Text);
await host.StopAsync();
```

Place this application-defined service after the top-level statements in `Program.cs`. It uses
fixed demonstration data, not a live weather API:

```csharp
public sealed class WeatherService
{
    public string GetForecast(string city) => $"{city}: sunny, 25 C (demo forecast).";
}
```

Reuse the session for later turns; it is backed by the same durable workflow and owns the
conversation history. For ambiguous-response recovery, persist and reuse a caller-supplied
`TemporalAgentRunOptions.RequestId`.

## 3. Split the client from the worker

The client process needs only the task queue and agent name; it does not register a chat client,
tools, or context providers:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTemporalClient(options => options.TargetHost = "localhost:7233");
builder.Services.AddTemporalAgentProxies("agents", "Assistant");

using var clientHost = builder.Build();
var agent = clientHost.Services.GetRequiredKeyedService<AIAgent>("Assistant");
var session = await agent.CreateSessionAsync();
Console.WriteLine((await agent.RunAsync("Weather in Kingston?", session)).Text);
```

---

## Rules worth knowing on day one

- **Tool activities use one attempt by default.** `TemporalAgentOptions.RetryPolicy` opts every tool on the
  agent into the same retry policy. The worker-level `DefaultRetryPolicy` applies to model and
  context preparation only and does not enable tool retries. Memory-scope initialization uses a
  fixed policy. Only configure
  retries when all affected tools are safe to retry.
- **One run at a time per session.** Overlapping `RunAsync` calls on the same session throw.
  Parallel conversations get a session each.
- **A session belongs to its agent.** Passing one agent's session to another is rejected.
- `ChatClientAgentOptions.ChatOptions.ResponseFormat` can be set at registration. At run time,
  `ChatClientAgentRunOptions` supports only `ChatOptions.ResponseFormat`; custom history providers,
  streaming, per-run chat-client factories, and other run-time chat-option overrides are rejected.

## Where to go next

| Need | Guide |
|---|---|
| Full builder and options reference | [usage.md](./usage.md) |
| Tool retries, timeouts, write safety | [durable-agents.md](./durable-agents.md) |
| Log or trace model calls | [llm-call-interception.md](./llm-call-interception.md) |
| Approval gates and human review | [hitl-patterns.md](./hitl-patterns.md) |
| Common mistakes | [dos-and-donts.md](./dos-and-donts.md) |
| A runnable starting point | `samples/MAF/BasicAgent` |
