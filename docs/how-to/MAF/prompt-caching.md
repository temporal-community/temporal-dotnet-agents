# History Management and Token Optimization

How conversation history grows in TemporalAgents, how the framework manages it across turns and continue-as-new boundaries, and strategies for controlling token costs.

---

## Table of Contents

1. [Overview](#overview)
2. [How History Grows](#how-history-grows)
3. [History Serialization Format](#history-serialization-format)
4. [Continue-as-New: Automatic History Transfer](#continue-as-new-automatic-history-transfer)
5. [Token Usage Monitoring](#token-usage-monitoring)
6. [Strategies for Reducing Token Costs](#strategies-for-reducing-token-costs)
7. [Context-Provider Boundary](#context-provider-boundary)
8. [StateBag Persistence](#statebag-persistence)

---

## Overview

Each agent session retains conversation history containing user messages, assistant responses,
tool calls, and tool results. This history is:

1. **Retained as workflow state** in `DurableSessionEntry` records. MAF sessions use `AgentSessionRequest` and `AgentSessionResponse`, which extend the shared MEAI entry types; each entry stores MEAI `ChatMessage` instances.
2. **Flattened and prepared at the start of a turn**, then sent to the model in durable activities. The accumulated message list is sent again for each model step in a tool loop.
3. **Carried across continue-as-new boundaries** with the currently retained conversation history.

By default, the model sees the complete retained conversation on every turn. Long-running sessions
can therefore increase model input and eventually exceed a model's context window.

---

## How History Grows

Each successful turn appends a request and response entry:

```
Turn 1:  [Request₁]  →  [Response₁]                    = 2 entries
Turn 2:  [Request₁] [Response₁] [Request₂]  →  [Response₂]  = 4 entries
Turn 3:  ... = 6 entries
Turn N:  ... = 2N entries
```

Most failed turns roll back their history changes. If a turn is interrupted after a tool was
dispatched, the request and an `Interrupted` response are retained so the possible side effect is
not hidden.

At the start of each turn, the workflow flattens the retained entries and prepares context. The
resulting message list is serialized into each model-step activity input and sent again for every
step, so Temporal event payloads also grow during a multi-step tool loop. Entries already contain
MEAI `ChatMessage` instances; no per-message conversion is needed.

Without history reduction or model-context pruning, a fixed amount of new content per turn makes
**cumulative model input grow quadratically**:
turn N sends the N previous exchanges plus the new message, so a 20-turn conversation sends
about 40 messages on its final turn.

---

## History Serialization Format

History entries use a polymorphic JSON format with a `$type` discriminator at the entry layer. The MAF-specific subclasses use the discriminators `agent_request` / `agent_response`; the AI library's own concrete types (in mixed-library scenarios) use `ai_request` / `ai_response`. Library-created entries also carry distinct stable `entryId` values and a shared `turnId` for each request/response pair; both survive continue-as-new. These identities are separate from `correlationId`, which supports log/trace correlation and can also pair entries when a turn ID is unavailable. Within each entry's `messages`, built-in MEAI `AIContent` types use MEAI's `$type` discriminator (for example `text`, `functionCall`, and `functionResult`) from `AIJsonUtilities.DefaultOptions`:

```json
[
  {
    "$type": "agent_request",
    "entryId": "request-entry-id",
    "turnId": "turn-id",
    "correlationId": "abc123",
    "createdAt": "2026-04-30T10:00:00Z",
    "messages": [
      {
        "role": "user",
        "contents": [{ "$type": "text", "text": "What is the weather?" }]
      }
    ]
  },
  {
    "$type": "agent_response",
    "entryId": "response-entry-id",
    "turnId": "turn-id",
    "correlationId": "abc123",
    "createdAt": "2026-04-30T10:00:01Z",
    "messages": [
      {
        "role": "assistant",
        "contents": [{ "$type": "text", "text": "The weather is sunny." }]
      }
    ],
    "usage": {
      "inputTokenCount": 42,
      "outputTokenCount": 8,
      "totalTokenCount": 50
    }
  }
]
```

The built-in content types supported by `AIJsonUtilities.DefaultOptions` are serialized directly;
application-defined content types may need their own serialization configuration. A response entry
can also carry provider-reported token usage.

---

## Continue-as-New: Automatic History Transfer

Temporal workflows accumulate event history independently of the agent's conversation history.
When continuing as new, the workflow starts a fresh run and carries the currently retained
conversation history and session state forward.

**What survives continue-as-new:**
- The currently retained conversation history (`CarriedHistory`), including any configured history
  reduction
- StateBag state (`CarriedStateBag`), such as compact provider scope identifiers
- The workflow's execution settings and session idle deadline

**What resets:**
- Temporal event history (the Temporal-level history, not conversation history)
- Run ID (changes to a new value)
- Timer commands are recreated in the new run; the carried session idle deadline is not restarted

The workflow ID remains the same, so the session ID continues to address the workflow; the run ID
changes.

### Monitor Temporal event-history pressure

Event-history limits are separate from retained conversation-entry and model-context limits. At an
event-count or history-size soft threshold, new turns are rejected while the workflow prepares to
continue as new. At the reserve threshold, pending approval waits expire so the active turn can
finish with headroom.
[`ITemporalAgentClient.GetHistoryBudgetAsync(sessionId)`](./usage.md#inspecting-workflow-history-budget)
is a read-only query that reports the latest observed event count and size, their thresholds,
admission/reserve and history-blocked status, retained entry count, and current/maximum duplicate
turn joiners. Matching joiners beyond the bound are rejected with a typed pending error.

---

## Token Usage Monitoring

Provider-reported token counts are available through spans, logs, and retained response entries:

### Per Model Step: OTel Span Attributes

The `agent.turn` span is emitted for a model step and can include token metrics from that response:

```
agent.turn
  gen_ai.usage.input_tokens  = 1542
  gen_ai.usage.output_tokens = 87
  gen_ai.usage.total_tokens  = 1629
```

These are set when the provider reports usage and no upstream MAF/MEAI OpenTelemetry middleware
owns the canonical usage attributes. See [Observability](./observability.md#agentturn-client-kind).

### Per Model Step: Structured Logs

`AgentActivities` logs provider-reported token counts for each model step:

```
[WeatherAgent/ta-weatheragent-abc123] Agent activity completed
  (Input tokens: 1542, Output tokens: 87, Total tokens: 1629)
```

### Per-Response: History State

Response entries store optional usage as `Microsoft.Extensions.AI.UsageDetails`. The public MAF
client does not expose a full-history query; use the spans and logs above for provider-reported
per-step usage.

### Aggregate: Search Attributes

When `EnableSearchAttributes = true`, the `TurnCount` search attribute lets you find high-activity sessions in the Temporal UI:

```
AgentName = "ResearchAgent" AND TurnCount > 20
```

---

## Strategies for Reducing Token Costs

### 1. Use Short-Lived Sessions

`TimeToLive` is an idle timeout, not a cap on the age or size of an active conversation. It renews
as turns run, so a short value reclaims idle sessions but does not limit history growth during an
active conversation:

```csharp
opts.AddDurableAgent("MyAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.TimeToLive = TimeSpan.FromHours(1);
});
```

After the idle timeout closes a session, a later send can start a new workflow run under the same
workflow ID; that new run starts with empty conversation history.

### 2. Summarize History Before Sending

Create a summarization step inside a workflow that condenses long histories before passing the
result to a specialist. This example assumes the application has registered an
`AgentInvocationActivities` activity that resolves the named agent proxy; that helper is
application code, not a package API. Invoke both agents through activities:

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

[WorkflowRun]
public async Task<string> RunAsync(string question)
{
    var agentCallOptions = new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            NonRetryableErrorTypes =
            [
                nameof(DurableTurnFailedException),
                nameof(DurableTurnOutcomeTooLargeException),
            ],
        },
    };
    var researchSession = WorkflowAgents.NewAgentSessionId("Researcher");
    string lastResponse = string.Empty;
    for (var i = 0; i < 5; i++)
    {
        lastResponse = await Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                "Researcher",
                researchSession.WorkflowId,
                Workflow.NewGuid().ToString("N"),
                $"Research step {i}: {question}"),
            agentCallOptions);
    }

    var summarySession = WorkflowAgents.NewAgentSessionId("Summarizer");
    return await Workflow.ExecuteActivityAsync(
        (AgentInvocationActivities activities) => activities.RunAsync(
            "Summarizer",
            summarySession.WorkflowId,
            Workflow.NewGuid().ToString("N"),
            $"Summarize these findings concisely: {lastResponse}"),
        agentCallOptions);
}
```

The summarizer sees only the final output, not the full 5-turn research history. All six
invocations use the same [bounded invocation policy](./usage.md#invoking-agents-from-a-workflow);
retrying a retained failure with the same request ID cannot start a fresh turn.

### 3. Use a Compatible Custom Context Projection

The workflow remains the authoritative conversation-history owner. A compatible
`AIContextProvider` may add retry-safe instructions or messages for a specific LLM step; it does
not replace durable history. Implement the projection in application code and, when another
provider or tool needs it, publish a compact namespaced value in the session `StateBag`.

Provider-owned history and external writes are not supported direct durable registrations because
the provider lifecycle runs in retryable activities without an atomic idempotent persistence
contract. See [Context Providers](context-providers.md) for supported
patterns and [Bounded Durable `ChatClientAgent` Compatibility](../../architecture/MAF/bounded-durable-agent-compatibility.md) for exclusions.

### 4. Use One-Shot Sessions for Independent Tasks

For tasks that don't need conversational context, use a fresh session per request:

```csharp
// Each call starts fresh — no history accumulation
var session = new TemporalAgentSessionId("AnalystAgent", Guid.NewGuid().ToString("N"));
var response = await client.SendAsync(
    session,
    new RunRequest("Analyze this data: ...")
    {
        CorrelationId = Guid.NewGuid().ToString("N"),
    });
```


> **Note:** The `RunAgentAsync(string agentName, string message)` convenience overload is deprecated. Use `SendAsync(TemporalAgentSessionId, RunRequest)` directly.

### 5. Reduce Retained History at Continue-as-New

<a id="5b-cap-history-at-a-fixed-size-with-maxentrycount"></a>

`TemporalAgentsOptions.DefaultMaxEntryCount` is the retained-history threshold that starts rollover
handling, not a hard cap. When the count threshold requires reduction, a keyed history reducer must
produce a valid smaller history before the workflow continues-as-new. Without one, the workflow
enters `HistoryBlocked` until explicit recovery; it does not silently trim or discard old entries.
The threshold-only setup below therefore needs a reducer before it reaches that threshold:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
        opts.DefaultMaxEntryCount = 50;  // start history rollover handling at 50 entries
    });
```

Pair the bound with a keyed history reducer to control which entries are retained at the boundary.
Register a deterministic reducer in the worker's DI container:

```csharp
builder.Services.AddKeyedSingleton<Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>>(
    "keep-recent-30",
    (_, _) => MyHistoryReducers.KeepRecentCompleteTurns);
```

Set `DefaultHistoryReducerKey = "keep-recent-30"` in the same `AddTemporalAgents` options callback
as `DefaultMaxEntryCount`, or set `HistoryReducerKey` for one agent.

When a keyed reducer is configured and a reduction is needed, the workflow dispatches a Temporal
activity that resolves it and passes the retained history. The activity result becomes the initial
history for the new run and is recorded in workflow history. Without a configured reducer, a
required reduction enters `HistoryBlocked` and preserves the last valid history; the library does
not silently trim. `RetryHistoryCompactionAsync` retries the configured reducer, while
`TrimBlockedHistoryAsync` is the separately authorized, one-time lossy recovery. A custom reducer
must retain complete request/response turns, preserve ordering, and retain protected current,
system/developer-instruction, approval, or interrupted content, as well as incomplete entries;
simple entry-count slicing can split a turn and will be rejected.

### 6. Bound the Estimated Model Context

History-entry limits and model-context limits protect different resources. Configure an optional
estimated input-token limit and reserve output tokens per agent or as worker defaults:

```csharp
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxModelContextTokens = 8192;
            agent.ModelOutputTokenReserve = 1024;
        });
    });
```

The total limit includes the output reserve. Before each model call, the worker may remove only
complete, eligible prior turns; it preserves the current turn, approvals, interrupted records, and
other protected content. Context-provider messages and final instructions, tools, and response
schema are checked before dispatch too. If those additions or protected content do not fit, the
activity returns a non-retryable `DurableContextBudgetExceeded` failure rather than dropping more
content. The default estimator is a deterministic byte-based heuristic and is not an exact count
for a provider tokenizer. Register an `IDurableChatTokenEstimator` in worker DI to replace it with
a model-specific estimate. This limit does not constrain Temporal payload sizes or the already
recorded event history.

### 7. Request Structured Output When Needed

Use structured output when the caller needs machine-readable data. It is not inherently more
token-efficient: the schema also contributes to model input, and output size depends on the task:

```csharp
var report = await agent.RunStructuredAsync<WeatherReport>(messages, session);
```

### 8. Filter Tools per Request

Disable unnecessary tools to reduce model input. Tool definitions consume context, though their
token cost depends on the provider and tokenizer:

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather"],  // only this tool is available
    // EnableToolCalls = false          // or disable all tools
};
```

---

## Context-Provider Boundary

For a detailed explanation of `AIContextProvider` and `AgentSessionStateBag`, see [Session StateBag & Context Providers](../../architecture/MAF/session-statebag-and-context-providers.md).

Provider hooks run in retryable activities: `InvokingAsync` during turn preparation and
`InvokedAsync` after a model response. A retry can run these hooks again, so external side effects
must be idempotent; they are not committed atomically with workflow progress. Use providers for
compatible auxiliary projections, not as an external conversation-history store.

---

## StateBag Persistence

`AgentSessionStateBag` carries compact provider state (for example, a tenant identifier) across
turns without serializing an external data store:

```
Turn 1: Activity starts → empty StateBag
        Provider writes: bag["app.tenant"] = "acme"
        Activity ends → bag serialized → workflow stores it

Turn 2: Activity starts → bag restored from workflow state
        Provider reads: bag["app.tenant"] → "acme"
        Activity ends → bag re-serialized

Continue-as-New:
        carriedStateBag = _currentStateBag  → new workflow run
        Bag restored seamlessly in the next turn
```

An empty bag contributes no serialized StateBag payload.

---

## References

- [Session StateBag & Context Providers](../../architecture/MAF/session-statebag-and-context-providers.md) — AIContextProvider deep dive
- [Observability](./observability.md) — token usage monitoring via OTel spans
- [Usage Guide](./usage.md) — structured output and tool filtering

---
