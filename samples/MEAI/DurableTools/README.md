# DurableTools: Per-Tool Activity Dispatch

## Overview

This sample demonstrates `AsDurable()`, which wraps an `AIFunction` so that each tool invocation
dispatches as its own independent Temporal activity rather than running inline inside the LLM
activity. Each tool call gets its own retry policy, timeout, and event history entry — visible
individually in the Temporal Web UI.

- `AsDurable()` — wraps any `AIFunction` so workflow context triggers `DurableFunctionActivities`
- `AddDurableTool()` registers one function; `AddDurableTools()` registers an ordered collection
  with the default policy
- `WeatherReportWorkflow` — custom workflow that calls a durable tool directly (not via `DurableChatSessionClient`)
- Per-tool retry isolation: a failing tool is retried without re-running the LLM call
- Bounded defaults: an omitted policy allows at most five attempts, with exponential backoff capped
  at 30 seconds. This is not exactly-once execution; configure `MaximumAttempts = 1` for an unsafe
  non-idempotent effect.

## Architecture

```
Program.cs
    │
    ├─ AddDurableTool(weatherTool)  →  DurableFunctionRegistry["get_current_weather"]
    │
    └─ DurableToolDemo.RunAsync()
           │
           └─ WeatherReportWorkflow.RunAsync()
                  │
                  └─ durableWeather.InvokeAsync()  →  DurableFunctionActivities
                                                       └─ registry["get_current_weather"]
                                                              └─ GetCurrentWeather(city)
```

## Highlights

- **Direct custom-workflow tool dispatch.** `AsDurable()` is for a workflow that explicitly invokes a known function. Managed `DurableChatSessionClient` tool calls use `AddDurableTool()`/`AddDurableTools()` and the workflow-owned model/tool loop instead.
- **Stub inner function.** The lambda passed to `AIFunctionFactory.Create` inside `WeatherReportWorkflow` is never reached — `Workflow.InWorkflow == true` intercepts the call before the stub executes. The real implementation lives in `Program.cs` and is resolved from the `DurableFunctionRegistry` by name.
- **Registry lookup by name.** `DurableFunctionActivities` resolves functions from `DurableFunctionRegistry` using the function's `Name` property as the key. The name must match between the workflow-side stub and the `AddDurableTools` registration.
- **No `DurableChatSessionClient` required.** `AsDurable()` works in any `[Workflow]` class — you are not limited to the stock `DurableChatWorkflow` session model.
- **Same task queue.** The function activity runs on the calling workflow's task queue, whose
  worker registers the implementation with `AddDurableTool()`. Setting
  `DurableExecutionOptions.TaskQueue` on `AsDurable()` does not reroute the function activity.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)

> No API key required — `GetCurrentWeather` is a mock that returns random strings.
> This sample exercises the durable activity dispatch path only; no LLM is involved.

### Run

```bash
dotnet run --project samples/MEAI/DurableTools/DurableTools.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 AsDurable() — Per-Tool Activity Dispatch
════════════════════════════════════════════════════════
 Each tool call is a separate Temporal activity with its
 own retry policy, timeout, and event history entry.

 Workflow ID: weather-report-<guid>
 City      : Tokyo

 Result: It's sunny and 22 °C in Tokyo.
════════════════════════════════════════════════════════
```
