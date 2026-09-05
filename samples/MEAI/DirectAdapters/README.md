# DirectAdapters: Activity + `AIFunction.AsDurable()`

## Overview

This sample demonstrates the recommended low-level pattern for a fully custom `[Workflow]` that
needs one or two individual durable LLM/tool calls and nothing more — none of the
session/history/HITL machinery that `DurableChatSessionClient` or `DurableChatWorkflowBase<TOutput>`
provide.

**Most applications that own a custom workflow and want durable LLM calls should reach for
[`samples/MEAI/CustomWorkflow`](../CustomWorkflow/README.md) first** — it gives you history, HITL,
and continue-as-new for free, and shows the same hand-written-Activity shape used here combined
with an inline tool-invocation loop. Reach for the pattern shown here only when you deliberately
want *none* of that session machinery.

- `ResearchActivities` — a small, hand-written Activity class (constructor-injected `IChatClient`,
  one `[Activity]` method) that makes a single LLM call durable. No durable-adapter ceremony:
  standard DI, standard `IChatClient.GetResponseAsync(...)`, standard Temporal activity dispatch.
- `AsDurable()` — wraps an `AIFunction` so each tool invocation dispatches to
  `DurableFunctionActivities` as its own Temporal activity.
- `ResearchWorkflow` — a fully custom `[Workflow]` that composes both: one durable tool call feeds
  its result into one durable LLM call.

## Architecture

```
Program.cs
    │
    ├─ AddDurableTool(weatherTool)  →  DurableFunctionRegistry["get_current_weather"]
    ├─ AddChatClient(openAiChatClient)  (worker-side IChatClient, injected into ResearchActivities)
    ├─ AddSingletonActivities<ResearchActivities>()
    │
    └─ DirectAdaptersDemo.RunAsync()
           │
           └─ ResearchWorkflow.RunAsync()
                  │
                  ├─ weatherTool.AsDurable().InvokeAsync()  →  DurableFunctionActivities
                  │                                            └─ registry["get_current_weather"]
                  │
                  └─ Workflow.ExecuteActivityAsync(ResearchActivities.SummarizeWeatherAsync)
                                                        └─ openAiChatClient
```

## Highlights

- **A hand-written Activity, not a durable-adapter middleware chain.** `ResearchActivities` is
  ordinary DI-friendly Temporal activity code — constructor-injected `IChatClient`, a single
  `[Activity]` method, no sentinel types or `ChatClientBuilder` composition required inside
  workflow code. This is the same shape `samples/MEAI/CustomWorkflow`'s `ShoppingActivities` uses.
- **Two independent durable primitives, not one pipeline.** The Activity dispatches a single LLM
  call per invocation — it does not run an inline tool loop. `AsDurable()` is a separate wrapper
  for a single tool call, called directly by workflow code.
- **`AsDurable()` always uses the calling workflow's own task queue.** Its worker must be the same
  one that called `AddDurableTools(weatherTool)`.
- **`RegisterDefaultWorkflow = false`.** This sample never creates a `DurableChatSessionClient`, so
  the stock `DurableChatWorkflow` is suppressed; `ResearchWorkflow` is registered directly instead.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)
- An OpenAI-compatible API key (`OPENAI_API_KEY`)
- An OpenAI-compatible base URL (`OPENAI_API_BASE_URL`) — required; `Program.cs` throws
  `InvalidOperationException` if missing
- Optionally, `OPENAI_MODEL` to override the default (`gpt-4o-mini`)

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DirectAdapters
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DirectAdapters
```

### Run

```bash
dotnet run --project samples/MEAI/DirectAdapters/DirectAdapters.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 Direct Workflow Adapters — Activity + AsDurable()
════════════════════════════════════════════════════════
 One durable tool call (AsDurable) feeds one durable LLM call
 (a hand-written Activity) — no session, history, or HITL machinery.

 Workflow ID: research-<guid>
 City      : Seattle

 Summary: It's sunny and 22 °C in Seattle — a clear, mild day.
════════════════════════════════════════════════════════

Done.
```
