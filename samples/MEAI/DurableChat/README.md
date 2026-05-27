# DurableChat: Multi-Turn Durable Conversations

> **Looking for Pattern 1** (the simpler inline tool-call loop via `UseFunctionInvocation()`)? See `docs/how-to/MEAI/tool-functions.md` Model 1 — a dedicated sample is planned.

## Overview

This sample demonstrates durable multi-turn chat via `DurableChatSessionClient`, with Pattern 3
(per-tool activity dispatch via `AddDurableTools()`) for tool calls. Each `ChatAsync` call issues a
`[WorkflowUpdate]` against a long-lived Temporal workflow, so conversation history survives worker
restarts without any extra persistence code. Four demos run in sequence: multi-turn context
carry-over, an auto-populated tool call, history retrieval, and a focused Pattern 3 walk-through
with explicit-tools and auto-populated scenarios.

## Highlights

- **Durable session via `DurableChatSessionClient`.** Each turn is a workflow update, not a bare
  HTTP call. The conversation ID maps 1:1 to a Temporal workflow ID — the same ID routes all turns
  to the same workflow instance.
- **Multi-turn history carry-over.** The second question ("What is the population of that city?")
  is answerable because the workflow holds the prior exchange. The caller sends only the new
  message — the workflow supplies the full history to the LLM automatically.
- **Pattern 3 tool dispatch.** `AddDurableTools()` registers tools without
  `UseFunctionInvocation()`. `DurableChatWorkflow` runs the dispatch loop: each LLM call is a
  `GetChatStepAsync` activity; each tool call is its own `InvokeFunctionAsync` activity (visible
  in the Temporal Web UI). Per-tool retry and timeout configurable via `DurableChatToolOptions`
  (e.g. `opts.WithTimeout(...)`, `opts.NoRetry()`).
- **`GetHistoryAsync` for replay.** Sends a Temporal Query to the running workflow and returns the
  full `DurableSessionEntry` log — user, assistant, and tool messages.
- **`DurableAIDataConverter` is required.** Without it, `FunctionCallContent`,
  `FunctionResultContent`, and other `AIContent` subtypes lose their `$type` discriminator when
  serialized into workflow history, causing deserialization errors on replay. Set automatically
  when the client is connected via `DurableAIDataConverter.Instance` (see `Program.cs`).
- **Sessions have a TTL.** `opts.SessionTimeToLive` controls how long the workflow waits for a
  new turn before shutting down. Set it to match your expected idle window.

## Architecture: Pattern 3 dispatch loop

```
ChatAsync(conversationId, messages)
        │
        ▼
[WorkflowUpdate] DurableChatWorkflow
        │
        ▼
GetChatStepAsync activity         ← one LLM call
        │
        ▼  [if FunctionCallContent in response]
InvokeFunctionAsync × N           ← one activity per tool call, fanned out
        │                            in parallel via Workflow.WhenAllAsync
        ▼
loop back to next GetChatStepAsync until IsFinal
                                  (or MaxToolCallsPerTurn exceeded)
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key
- `OPENAI_API_BASE_URL` (required — `Program.cs` validates this at startup)
- `OPENAI_MODEL` (optional — defaults to `gpt-4o-mini`)

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableChat
# Optional:
dotnet user-secrets set "OPENAI_MODEL" "gpt-4o-mini" --project samples/MEAI/DurableChat
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

════════════════════════════════════════════════════════
 Demo 2: Tool Call (auto-populated from registry)
════════════════════════════════════════════════════════
 Conversation ID: tool-call-<guid>

 User : What is the weather like in Seattle right now?
 Agent: It's sunny and 22 °C in Seattle. ...
════════════════════════════════════════════════════════

════════════════════════════════════════════════════════
 Demo 3: History Query
════════════════════════════════════════════════════════
 Conversation ID: history-<guid>

 Persisted history:
   [User ] Name three planets in our solar system.
   [Agent] Mercury, Venus, and Earth are three planets ...
   [User ] Which of those is closest to the Sun?
   [Agent] Mercury is the closest to the Sun ...

 Total entries stored: 4 (4 messages)
════════════════════════════════════════════════════════

════════════════════════════════════════════════════════
 Demo 4: Durable Tool Dispatch (Pattern 3)
════════════════════════════════════════════════════════
 Tools registered via AddDurableTools() — each tool call
 becomes its own Temporal activity. Verify in Web UI at
 http://localhost:8233.

 ── Scenario 1: explicit ChatOptions.Tools = [weatherTool] ──
 Conversation ID: durable-tools-explicit-<guid>
 User : What is the weather like in Tokyo right now?
 Agent: It's overcast and 15 °C in Tokyo. ...

 ── Scenario 2: ChatOptions.Tools = null (auto-populated) ──
 Conversation ID: durable-tools-auto-<guid>
 User : Compare the weather in Paris and Berlin right now.
 Agent: In Paris it's sunny and 22 °C, while in Berlin ...

 Tool calls visible in Temporal Web UI as separate
 `Temporalio.Extensions.AI.InvokeFunction` activities.
════════════════════════════════════════════════════════

Done.
```

> Exact text varies by model. The `Total entries stored: N (M messages)` line reflects
> `history.Count` (raw `DurableSessionEntry` count) and `messages.Count` (flattened
> `ChatMessage` count) from `RunHistoryQueryDemoAsync` in `Program.cs`.

## Demo 1 — Multi-Turn Conversation

Plain text chat with no tools. Each `ChatAsync` call reuses the same `conversationId`, so the
workflow retains history and the second turn can answer a pronoun reference ("that city") without
the caller resending context.

## Demo 2 — Tool Call (auto-populated from registry)

Calls `ChatAsync` **without passing `ChatOptions.Tools`**. Because `weatherTool` is already
registered with `AddDurableTools(...)` on the worker, the `GetChatStepAsync` activity
auto-populates `Options.Tools` from the `DurableFunctionRegistry` before calling the LLM — the
caller doesn't need to repeat the tool list per call.

This is the right choice when:

- Every chat call should expose all registered tools to the LLM
- You don't need to narrow the per-call tool set
- You want the simplest possible setup (register once, just chat)

Behaviorally identical to Demo 4 Scenario 1 — both end up with the same `ChatOptions.Tools`
reaching the LLM. The difference is **who fills the list**: Demo 2 lets the activity
auto-populate; Demo 4 Scenario 1 demonstrates the explicit-control path for cases where you want
a subset.

> **Two distinct registrations.** `AddDurableTools(...)` registers the tool *implementation* on
> the worker. `ChatOptions.Tools` advertises the tool *schema* to the LLM. Both are required for
> a tool call to work — the worker needs to dispatch the function; the LLM needs to know the
> function exists. Auto-population just means the caller can skip the latter when they want every
> registered tool exposed.

## Demo 3 — History Query

Sends a Temporal `Query` to the running workflow via `GetHistoryAsync` and prints the persisted
`DurableSessionEntry` log. Includes user, assistant, and (when present) tool call / tool result
entries. The demo flattens `entry.Messages` to display individual `ChatMessage`s and reports both
counts: total entries vs. total messages.

## Demo 4 — Durable Tool Dispatch (Pattern 3)

Two sub-scenarios:

1. **Explicit `ChatOptions.Tools`** — the caller passes a specific subset of registered tools
   (e.g., `Tools = [weatherTool]` when many tools are registered but only one should be
   considered for this call). The workflow respects the explicit list and does NOT auto-populate.
2. **Auto-populated tools** — caller omits `ChatOptions` entirely. The activity auto-populates
   `Tools` from `DurableFunctionRegistry`, so all registered tools are available to the LLM. Same
   behavior as Demo 2 above.

### Verification in Temporal Web UI

1. Open `http://localhost:8233` while the sample is running (or after it finishes).
2. Find the workflow IDs printed by Demo 4 (e.g. `durable-tools-explicit-<guid>`,
   `durable-tools-auto-<guid>`).
3. Open the workflow history. You should see:
   - `Temporalio.Extensions.AI.GetChatStep` activities (one per LLM iteration)
   - `Temporalio.Extensions.AI.InvokeFunction` activities (one per tool call) — each with its
     Summary set to the tool name
4. Demos 2 and 4 both follow this structure since both use Pattern 3 (durable tool dispatch). The
   difference is *how* `ChatOptions.Tools` is populated, not whether the tool runs inside its own
   activity.

### Per-tool retry and timeout via `DurableChatToolOptions`

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(...)
    .AddDurableTools(weatherTool, opts => opts.WithTimeout(TimeSpan.FromSeconds(30)))
    .AddDurableTools(writeTool,   opts => opts.NoRetry());   // non-idempotent: don't double-execute
```

- `WithTimeout(TimeSpan)` — sets `StartToCloseTimeout` for this tool's activity
- `NoRetry()` — sets `MaximumAttempts = 1`; use for non-idempotent write-style tools
- `WithMaxAttempts(int)` — caps retries at N
- Direct `opts.RetryPolicy = new RetryPolicy { ... }` — for full control (custom backoff,
  non-retryable types)

### Choosing a custom workflow instead

If you need orchestration, branching, or multi-agent coordination beyond a single chat loop, see
`samples/MEAI/CustomWorkflow` and the `.AsDurable()` wrapping pattern (Pattern 2). For an
in-depth comparison of all three models, see `docs/how-to/MEAI/tool-functions.md`.

## Going to Production

This sample is shaped for clarity, not for a regulated production workload. Before you ship a durable chat surface, walk this list.

1. **Every conversation message is persisted, in plaintext, in workflow history.** `DurableChatWorkflow` accumulates every `ChatMessage` — user input, assistant output, `FunctionCallContent`, and `FunctionResultContent` — as workflow events. Anyone with read access to your Temporal namespace can read every conversation verbatim by querying history. Treat namespace ACLs as PII access controls, scrub or tokenize sensitive fields before they enter `ChatAsync`, and configure a payload codec (encryption-at-rest) on the data converter for any deployment handling regulated content. Don't ship raw user content to a namespace your operators can browse.

2. **Tools run with the worker's full ambient authority — not the LLM's.** Each `InvokeFunctionAsync` activity executes in the worker process with whatever credentials, network access, and database handles you wired into DI. If the LLM hallucinates a call to a write tool that you registered, the tool will execute. Authorization is the developer's responsibility: gate every side-effecting tool on caller identity, tenant scope, or an explicit approval step — never trust the model to refuse. Pattern 3's per-tool activity boundary makes this explicit but does not enforce it for you.

3. **Non-idempotent tools must call `opts.NoRetry()`.** Pattern 3 inherits the worker-level `RetryPolicy` (this sample sets `MaximumAttempts = 3`) for any tool that does not override it. Write tools — payments, emails, database mutations, external API calls with side effects — must pass `opts => opts.NoRetry()` to `AddDurableTools(...)`, or a transient activity failure will re-execute the side effect on retry. See lines 131-132 of `Program.cs` for the read-tool pattern; flip to `NoRetry()` for anything that mutates state.

4. **Secrets do not belong in `appsettings.json` or unscoped env vars.** `dotnet user-secrets` is correct for local dev only. In production, resolve `OPENAI_API_KEY` (and the Temporal mTLS material, if you use a Cloud namespace) from Azure Key Vault, AWS Secrets Manager, GCP Secret Manager, or a comparable store. Never commit keys, never log raw `IConfiguration` values, and audit CI pipelines for env-var echo. A leaked LLM key is a billing incident before it is a security one.

5. **Set `MaxToolCallsPerTurn` and `MaximumConsecutiveErrorsPerRequest` deliberately.** Defaults are `20` and `3` respectively (`DurableExecutionOptions.cs:155, 163`). A runaway model that loops on a misbehaving tool will burn 20 LLM calls per turn at full token cost before the sentinel fires. Set both based on your cost ceiling and failure-mode expectations — for high-volume traffic, drop `MaxToolCallsPerTurn` to 5-8 and consider `MaximumConsecutiveErrorsPerRequest = 0` (MAF-style immediate propagation) for workloads where silent retry is worse than a hard failure. See `docs/how-to/MEAI/usage.md` "Pattern 3 Loop Semantics."

6. **Decide on a workflow shutdown contract.** `host.StopAsync()` stops the worker process; it does not terminate running workflows on the Temporal server. Without action, every conversation parks for `SessionTimeToLive` (default 14 days) consuming workflow slots. This sample signals `"Shutdown"` to each tracked conversation before exit (`Program.cs:160-171`) — choose signal-and-drain, signal-and-cancel, or TTL-only based on whether mid-turn truncation is acceptable for your domain. For HIPAA, SOC 2, or data-residency workloads, also shorten `SessionTimeToLive` from the 14-day default and ensure session-end events trigger explicit termination.

7. **Conversation IDs leak into logs, metrics, and history search.** Treat the `conversationId` as a join key visible to anyone with observability access. Sample code uses random GUIDs; real applications often use customer or tenant IDs. If you must derive the ID from a stable identifier, hash or salt it before it becomes a workflow ID — once it lands in Temporal history, search attributes, and downstream metrics, you cannot retract it.
