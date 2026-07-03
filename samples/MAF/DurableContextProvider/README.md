# DurableContextProvider Sample

Demonstrates registering durable tools alongside a context provider using two approaches:

- **Approach A — `IDurableToolSource`**: Implement the interface on your `AIContextProvider`
  subclass. The framework calls `GetDurableTools()` once at registration and automatically
  registers the returned specs as Temporal activities.

- **Approach B — explicit `DurableToolRegistrationSpec`**: Pass specs as the `durableTools`
  parameter to `AddContextProvider(provider, durableTools)`. Use this when you don't own the
  provider type. The framework transparently wraps the provider in `DurableContextProviderWrapper`.

## Why durable dispatch matters for tools

Without `IDurableToolSource` or explicit `DurableToolRegistrationSpec` entries, a context
provider's tools execute in-process inside the `RunDurableAgentStep` activity — MAF's
`FunctionInvokingChatClient` handles them inline. If the worker crashes mid-tool, the tool
re-runs from scratch when the activity retries (double-execution risk for write operations).
The tool call also produces no individual entry in the Temporal event history, so it is
invisible in the Web UI.

With durable dispatch, each tool call becomes its own `InvokeAgentTool` activity: it appears
in the workflow history, has its own retry/timeout policy, and completed tool calls are never
re-executed on replay.

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
