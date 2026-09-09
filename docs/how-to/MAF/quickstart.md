# MAF Quickstart

One durable agent, registered and called, in about thirty lines.

`TemporalCommunity.Extensions.Agents` makes a Microsoft Agent Framework `AIAgent` durable with
Temporal: every LLM call is its own activity, every tool call is its own activity, and a crashed
worker resumes from event history instead of starting the conversation over.

Once this works, [usage.md](./usage.md) is the reference for everything else.

---

## Before you start

- A Temporal Service on `localhost:7233` — `temporal server start-dev`
- An `OPENAI_API_KEY`, via `dotnet user-secrets` or the environment

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
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<WeatherService>().GetWeather,
                "get_weather"));
        });
    });
```

Two things that bite people here:

- **Register `ITemporalClient` explicitly.** The three-argument
  `AddHostedTemporalWorker(address, namespace, queue)` overload configures a worker-internal client
  but does not put `ITemporalClient` in DI, and `AddTemporalAgents` needs one.
- **Do not call `.UseFunctionInvocation()`** on your chat client. The workflow owns tool dispatch;
  an in-process loop is rejected at runtime.

## 2. Call it from outside a workflow

```csharp
var proxy   = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();

var reply = await proxy.RunAsync("What's the weather in Kingston?", session);
Console.WriteLine(reply.Text);
```

Reuse that `session` for the rest of the conversation — it is what accumulates history. A second
independent conversation gets its own `CreateSessionAsync()`.

## 3. Or call it from inside a workflow

```csharp
[WorkflowRun]
public async Task<string> RunAsync(string question)
{
    var agent   = WorkflowAgents.GetTemporalAgent("Assistant");
    var session = await agent.CreateSessionAsync().ConfigureAwait(true);
    var reply   = await agent.RunAsync([new ChatMessage(ChatRole.User, question)], session)
        .ConfigureAwait(true);

    return reply.Messages[^1].Text ?? string.Empty;
}
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
