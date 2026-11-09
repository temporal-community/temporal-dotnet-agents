# DurableContextProvider Sample

Demonstrates registering durable tools alongside a context provider using two approaches:

- **Approach A — `IDurableToolSource`**: Implement the interface on your `AIContextProvider`
  subclass. The framework calls `GetDurableTools()` once at registration and automatically
  registers the returned specs as Temporal activities.

- **Approach B — explicit `DurableToolRegistrationSpec`**: Pass specs as the `durableTools`
  parameter to `AddContextProvider(provider, durableTools)`. Use this when you don't own the
  provider type. The framework transparently wraps the provider in `DurableContextProviderWrapper`.

## Why durable dispatch matters for tools

Without `IDurableToolSource` or explicit `DurableToolRegistrationSpec` entries, a managed provider
cannot introduce an executable tool. Preparation rejects untraceable functions before model
dispatch; they do not execute inline. Register through one of the two approaches above, then
select the exact registered functions seeded into the provider context.

With durable dispatch, each tool call becomes its own `InvokeAgentTool` activity: it appears
in the workflow history, has per-tool timeouts, and uses the per-agent retry policy. The worker-level
retry policy applies to model steps only. Completed tool calls are never re-executed on replay.

## When to use which approach

| Scenario | Approach |
|---|---|
| You own the provider type | Implement `IDurableToolSource` (Approach A) |
| You're wrapping a third-party provider | Pass explicit specs to `AddContextProvider` (Approach B) |
| Tools change at runtime | Neither — use `agent.AddTool()` directly |

## Non-idempotent write tools

Tool activities use one attempt by default. An explicit agent retry policy applies to every tool
on the agent:

```csharp
agent.AddContextProvider(
    myProvider,
    durableTools:
    [
        new DurableToolRegistrationSpec(sendEmailTool),
        new DurableToolRegistrationSpec(searchTool),  // also one attempt by default
    ]);
```

## What you'll see in the Temporal Web UI

After running the sample, open `http://localhost:8233` and inspect the two workflows:

- `RunDurableAgentStep` — one activity row per LLM call
- `InvokeAgentTool:SearchAgent:web_search` — each web search as its own Temporal activity
- `InvokeAgentTool:WeatherAgent:get_weather` — each weather lookup as its own Temporal activity

Temporal replays completed activities from history. A tool attempt interrupted by a worker crash
is retried only when the shared policy permits it; its external outcome may be uncertain.

## Prerequisites

Temporal Service 1.31.0 or newer is required. For local development:

```bash
temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/DurableContextProvider
```

## Run

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/DurableContextProvider/DurableContextProvider.csproj
```
