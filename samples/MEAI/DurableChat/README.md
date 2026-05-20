# DurableChat: Multi-Turn Durable Conversations

## Overview

This sample demonstrates how to make a multi-turn chat session durable using `DurableChatSessionClient`.
Each call to `ChatAsync` issues a `[WorkflowUpdate]` against a long-lived Temporal workflow, so conversation
history survives worker restarts without any extra persistence code. Four demos run in sequence:
multi-turn context carry-over, tool calls via `UseFunctionInvocation()` (Pattern 1), history retrieval,
and durable tool dispatch via `AddDurableTools()` (Pattern 3).

- `DurableChatSessionClient.ChatAsync` — each message is a workflow update, not a bare HTTP call
- Conversation ID maps 1:1 to a Temporal workflow ID — the same ID routes all turns to the same instance
- `GetHistoryAsync` retrieves the full message log, including tool call and tool result entries
- `DurableAIDataConverter.Instance` preserves MEAI's `$type` discriminator across workflow history round-trips
- `UseFunctionInvocation()` handles the LLM tool-call loop inside the activity — tool results are sent back to the model before the activity returns (Pattern 1)
- `AddDurableTools()` without `UseFunctionInvocation()` dispatches each tool call as its own Temporal activity (Pattern 3 — observable, per-tool retry/timeout)

## Highlights

- **Context carry-over without resending history.** The second question ("What is the population of that city?") is answerable because the workflow holds the prior exchange. The caller sends only the new message — the workflow supplies the full history to the LLM automatically.
- **`DurableAIDataConverter` is required.** Without it, `FunctionCallContent`, `FunctionResultContent`, and other `AIContent` subtypes lose their `$type` discriminator when serialized into workflow history, causing deserialization errors on replay.
- **Two ways to run tools.** Pattern 1 (`UseFunctionInvocation()`) keeps the whole tool-call loop inside one activity — simple, one history entry per turn, no per-tool retry. Pattern 3 (`AddDurableTools()` without `UseFunctionInvocation()`) makes each tool call its own activity — observable in Temporal Web UI, supports per-tool retry/timeout via `DurableChatToolOptions`.
- **Sessions have a TTL.** `opts.SessionTimeToLive` controls how long the workflow waits for a new turn before shutting down. Set it to match your expected idle window.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableChat
```

### Run

```bash
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 Demo 1: Multi-Turn Conversation
════════════════════════════════════════════════════════
 Conversation ID: multi-turn-<guid>

 User : What is the capital of France?
 Agent: The capital of France is Paris.

 User : What is the population of that city?
 Agent: Paris has a population of approximately 2.1 million ...
════════════════════════════════════════════════════════

(Demo 2 — Pattern 1 tool call — output omitted for brevity; shows the model invoking
 get_current_weather inline within a single GetResponseAsync activity.)

════════════════════════════════════════════════════════
 Demo 3: History Query
════════════════════════════════════════════════════════
 Persisted history:
   [User ] Name three planets in our solar system.
   [Agent] Mercury, Venus, and Earth are three planets ...
   ...
 Total messages stored: 4
════════════════════════════════════════════════════════

(Demo 4 — Pattern 3 durable tool dispatch — output shows scripted weather lookups.
 Each tool call appears in Temporal Web UI as its own InvokeFunction activity.)
```

## Demo 1 — Multi-Turn Conversation

Plain text chat. Each `ChatAsync` call reuses the same `conversationId`, so the workflow retains history and the second turn can answer a pronoun reference ("that city") without the caller resending context.

## Demo 2 — Tool Call (Pattern 1, `UseFunctionInvocation()`)

The chat client pipeline includes `UseFunctionInvocation()`. When the LLM requests a tool call, MEAI's middleware runs the tool synchronously, sends the result back to the LLM, and returns the final assistant message — **all inside one `GetResponseAsync` activity**. The whole tool round-trip is invisible at the Temporal activity level: you see one chat activity, not one per tool.

This is the right choice when:
- Tools are fast and idempotent
- You don't need per-tool retry policies
- You want the simplest possible setup

## Demo 3 — History Query

Sends a Temporal `Query` to the running workflow via `GetHistoryAsync` and prints the persisted `DurableSessionEntry` log. Includes user, assistant, and tool messages.

## Demo 4 — Durable Tool Dispatch (Pattern 3, `AddDurableTools()`)

Registers tools via `AddDurableTools()` on the worker builder. The chat client pipeline does **not** call `UseFunctionInvocation()` — instead, `DurableChatWorkflow` automatically runs a dispatch loop:

```
GetChatStepAsync activity      ← one LLM call
  ↓  [if FunctionCallContent in response]
InvokeFunctionAsync × N         ← one Temporal activity per tool call (parallel fan-out)
  ↓
loop back to next LLM call until IsFinal
```

**Two sub-scenarios demonstrated:**

1. **Explicit `ChatOptions.Tools`** — the caller passes a specific subset of registered tools. The workflow respects the explicit list.
2. **Auto-populated tools** — caller passes `null` (or no `ChatOptions.Tools`). The activity auto-populates from `DurableFunctionRegistry`, so all registered tools are available to the LLM.

**Verification in Temporal Web UI:**

1. Open `http://localhost:8233` while the sample is running (or after it finishes).
2. Find the workflow IDs printed by Demo 4 (e.g. `durable-tools-<guid>`).
3. Open the workflow history. You should see:
   - `Temporalio.Extensions.AI.GetChatStep` activities (one per LLM iteration)
   - `Temporalio.Extensions.AI.InvokeFunction` activities (one per tool call) — each with its Summary set to the tool name
4. Contrast with Demo 2's workflow history, which shows a single `Temporalio.Extensions.AI.GetResponse` activity per turn — the tool call is invisible at the activity level because it ran inline.

**Per-tool retry and timeout via `DurableChatToolOptions`:**

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(...)
    .AddDurableTools(weatherTool, opts => opts.WithTimeout(TimeSpan.FromSeconds(30)))
    .AddDurableTools(writeTool, opts => opts.NoRetry());   // non-idempotent: don't double-execute
```

- `WithTimeout(TimeSpan)` — sets `StartToCloseTimeout` for this tool's activity
- `NoRetry()` — sets `MaximumAttempts = 1`; use for non-idempotent write-style tools
- `WithMaxAttempts(int)` — caps retries at N
- Direct `opts.RetryPolicy = new RetryPolicy { ... }` — for full control (custom backoff, non-retryable types)

**When to choose Pattern 3 over Pattern 1:**

| | Pattern 1 (`UseFunctionInvocation`) | Pattern 3 (`AddDurableTools`) |
|---|---|---|
| Tool execution | Inline inside one activity | Each tool is its own activity |
| Per-tool retry / timeout | No | Yes (via `DurableChatToolOptions`) |
| Observability | One history entry per turn | One entry per LLM call + one per tool |
| Setup | `UseFunctionInvocation()` in chat client chain | `AddDurableTools()` on worker builder, no `UseFunctionInvocation()` |
| Custom workflow required | No | No |

If you need a custom workflow (orchestration, branching, parallel agents), see `samples/MEAI/CustomWorkflow` and the `.AsDurable()` wrapping pattern (Pattern 2).
