# DurableContextProvider Sample

Demonstrates registering durable tools alongside a context provider using two approaches:

- **Approach A — `IDurableToolSource`**: Implement the interface on your `AIContextProvider`
  subclass. The framework calls `GetDurableTools()` once at registration and automatically
  registers the returned specs as Temporal activities.

- **Approach B — explicit `DurableToolRegistrationSpec`**: Pass specs as the `durableTools`
  parameter to `AddContextProvider(provider, durableTools)`. Use this when you don't own the
  provider type. The framework transparently wraps the provider in `DurableContextProviderWrapper`.

## When to use which approach

| Scenario | Approach |
|---|---|
| You own the provider type | Implement `IDurableToolSource` (Approach A) |
| You're wrapping a third-party provider | Pass explicit specs to `AddContextProvider` (Approach B) |
| Tools change at runtime | Neither — use `agent.AddTool()` directly |

## Non-idempotent write tools

Write tools (send email, apply refund, write file) **must** call `opts.NoRetry()` to prevent
double-execution on activity retry:

```csharp
agent.AddContextProvider(
    myProvider,
    durableTools:
    [
        new DurableToolRegistrationSpec(sendEmailTool, opts => opts.NoRetry()),
        new DurableToolRegistrationSpec(searchTool),  // read-only — default retry is fine
    ]);
```

## What you'll see in the Temporal Web UI

After running the sample, open `http://localhost:8233` and inspect the two workflows:

- `RunDurableAgentStep` — one activity row per LLM call
- `InvokeAgentTool:SearchAgent:web_search` — each web search as its own Temporal activity
- `InvokeAgentTool:WeatherAgent:get_weather` — each weather lookup as its own Temporal activity

Worker crashes during any tool call are safe: Temporal replays completed activities from history
and retries only the failed one.

## Prerequisites

```bash
temporal server start-dev
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/DurableContextProvider
```

## Run

```bash
dotnet run --project samples/MAF/DurableContextProvider/DurableContextProvider.csproj
```
