# Basic Agent: Single-Process Durable Conversation

## Overview

The simplest way to get started with `TemporalCommunity.Extensions.Agents`. A single process registers a Temporal worker and an AI agent, then runs a multi-turn conversation where every exchange is durably recorded in workflow state.

This sample demonstrates:
- Registering an agent with MAF `ChatClientAgentOptions` and Temporal-only `TemporalAgentOptions`
- Resolving the keyed MAF `AIAgent` proxy for the durable session
- Dispatching a tool as a durable activity with an invocation-scoped service dependency
- Opening a session with `CreateSessionAsync()` and sending turns with `RunAsync()`
- Multi-turn context: the workflow owns conversation history and includes it in each LLM call

## Highlights

- **WorkflowUpdate as request/response.** Each `RunAsync()` call is a Temporal `[WorkflowUpdate]` — the caller blocks until the agent responds, with no polling loop required.
- **History lives in the workflow.** Conversation context is stored in `AgentWorkflow` state, not in the client. Any authorized process with the session ID can send a follow-up. At the configured entry limit, history is not silently trimmed: configure a keyed retained-history reducer or inspect `GetHistoryBudgetAsync()` and use the explicit recovery updates on `ITemporalAgentClient`.
- **Sessions are lazy.** `CreateSessionAsync()` creates a local session token — no Temporal workflow exists yet. The `AgentWorkflow` starts on the first `RunAsync()` call. Querying the workflow handle before sending any message will return "workflow not found".
- **Tool calls are durable per-tool activities.** `get_weather` executes inside its own `InvokeAgentTool` activity. If the worker restarts between the tool call and the model response, the tool result is replayed from history.
- **Scoped tool dependencies.** The sample tool receives `IServiceProvider` through `AIFunctionArguments.Services`; scoped services are resolved for each tool invocation rather than captured at registration time.
- **Turn cancellation is explicit.** Resolve `ITemporalAgentClient` and call `CancelTurnAsync(session.SessionId, requestId)` with the ID supplied to `RunAsync`. Cancelling the caller's `RunAsync` RPC wait does not cancel the accepted turn. The workflow propagates cancellation to active model/tool activities and waits for their cancellation to complete before stopping. Activity code must cooperate, and cancellation never rolls back an external tool effect.
- **Ambiguous sends are read-only to reconcile.** After a call with a persisted `RequestId`, use `ITemporalAgentClient.GetTurnOutcomeAsync(session.SessionId, requestId)` to inspect Pending, Succeeded, Failed, or Unavailable. Unavailable does not prove the request was never accepted; reconcile before deciding whether to retry.
- **Single-process simplicity.** Worker, agent, and caller all live in the same `IHost` — the minimum viable setup before splitting into separate processes.

## Recover an ambiguous dispatch

`TurnRecoveryExample.cs` persists both IDs to a new receipt file **before** dispatch. The demo
cancels only the caller's RPC wait after 200 ms, then queries the outcome; this illustrates an
ambiguous acknowledgement, not actual lost transport completion:

```bash
dotnet run --project samples/MAF/BasicAgent -- --recovery-demo ./turn-receipt.json
```

To resume reconciliation in a fresh process, use the same server/namespace and receipt:

```bash
dotnet run --project samples/MAF/BasicAgent -- --recover ./turn-receipt.json
```

The dispatch demo refuses to overwrite an existing receipt. Recover with that receipt or choose a
new path; do not discard a receipt while its turn may still be unresolved.

Recovery never dispatches a turn. It polls `Pending` at most six times, with backoff from 250 ms
to 2 s and a 5 s deadline per query. `Unavailable` requires authoritative reconciliation, not an
automatic resend; query exceptions and an exhausted pending budget are reported separately.
Failed outcomes expose retained tool-call summaries/counts. A retryable classification does not
prove a tool effect is safe to repeat. Keep the receipt after an ambiguous result; the default
conversation path is unchanged.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer. Search attributes are enabled by default; start a local server with `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`.
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/BasicAgent
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/BasicAgent
```

### Run

```bash
dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj
```

### Expected Output

```
Worker started. Sending messages...

Session workflow ID: ta-assistant-<guid>

User : What is the capital of France?
Agent: The capital of France is Paris.
Retained turn outcome: Succeeded

User : What is its population?
Agent: Paris has a population of approximately 2.1 million in the city proper...

User : What is the weather like there right now?
Agent: The current weather in Paris is sunny. (result is random — get_weather returns "It's sunny" or "It's raining")

Done.
```
