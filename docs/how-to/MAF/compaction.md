# In-Session History Compaction

How to bound the cost of long agent sessions by automatically compacting older turns into a summary marker — preserving the audit trail in your external store while keeping the LLM's input bounded.

> **Status**: `[Experimental("TA002")]` — wire format is stable but the API surface (`UseCompaction`, `ICompactionStrategy`, `CompactionContext`) may refine based on production feedback. Pin the dependency to a tagged release if you depend on the exact shape.

---

## Table of Contents

1. [What It Is](#what-it-is)
2. [When to Use It](#when-to-use-it)
3. [Quick Start](#quick-start)
4. [Built-in Strategies](#built-in-strategies)
5. [Custom Strategies](#custom-strategies)
6. [How It Works](#how-it-works)
7. [Compaction + External History Store](#compaction--external-history-store)
8. [Compaction + History Reducer](#compaction--history-reducer)
9. [GDPR Erasure Cascades](#gdpr-erasure-cascades)
10. [Observability](#observability)
11. [Mixed-Version Replay](#mixed-version-replay)
12. [Limitations](#limitations)
13. [References](#references)

---

## What It Is

`UseCompaction` is an opt-in feature on `DurableAgentBuilder` that lets the Agents library detect when a session has accumulated enough history to warrant a rollup, and then dispatches a separate `CompactHistory` Temporal activity that replaces the older portion of history with a single `CompactionMarkerEntry`.

The marker carries:
- The correlation IDs of the source entries it replaces (for audit + erasure cascade)
- The named strategy that produced it (`"truncation"`, `"sliding-window"`, `"summarization"`, or a custom key)
- The model ID (for summarization; empty string for non-LLM strategies)
- Optionally, a rollup summary in its `Messages` field (summarization only)

Subsequent `LoadAsync(applyCompaction: true)` calls collapse the marker in place — the LLM sees the rollup summary instead of the full pre-compact run, but the audit canonical view (`applyCompaction: false`) still contains every original entry.

---

## When to Use It

- **Long sessions where context window growth is the constraint.** A summarization strategy keeps the model's prompt under the window limit indefinitely.
- **Cost optimization on long-running agents.** Compaction reduces input tokens per turn while preserving conversation context.
- **Compliance-friendly retention.** Compaction is non-destructive — the audit canonical view (`applyCompaction: false`) still contains every original entry. GDPR erasure cascades work through `CompactionAwareErasureHelper`.

Compaction is **not** a replacement for `HistoryReducer` (continue-as-new-time deterministic reduction) or the agent's `MaxToolCallsPerTurn` cap. The three layers compose — see [Compaction + History Reducer](#compaction--history-reducer).

---

## Quick Start

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedTemporalWorker(
    targetHost: "localhost:7233",
    @namespace: "default",
    taskQueue: "agents")
    .AddTemporalAgents(opts =>
    {
        // Compaction REQUIRES an external history store — markers live there.
        opts.HistoryStore = sp => sp.GetRequiredService<IAgentHistoryStore>();

        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.Instructions = "You are a helpful support agent.";

            // Pick a built-in strategy by name.
            agent.CompactionStrategyKey = "summarization";
        });
    });

builder.Services.AddSingleton<IAgentHistoryStore, MyCompactionAwareStore>();
builder.Services.AddSingleton<IChatClient>(...);

using var host = builder.Build();
await host.RunAsync();
```

After the agent's history exceeds the strategy's threshold (default 30 entries for `summarization`), each turn that completes will dispatch a `CompactHistory` activity, which:

1. Loads the audit canonical view from your store
2. Invokes the strategy (which for `"summarization"` issues a chat completion to roll up older entries)
3. Appends one `CompactionMarkerEntry` to the store via `AppendAsync`

Subsequent turns load the projected view automatically — the LLM sees the summary, not the full history.

---

## Built-in Strategies

The three built-in strategies are pre-registered under their canonical keyed-DI names by `AddTemporalAgents`. Set `agent.CompactionStrategyKey` (or `opts.DefaultCompactionStrategy` for a worker-level default) to one of:

| Key | Default thresholds | Marker carries summary? | Trigger cadence |
|---|---|---|---|
| `"truncation"` | trigger=30, keep=10 | No — `Messages` is empty | One-shot when count >> threshold |
| `"sliding-window"` | windowSize=20 | No | Continuous — every turn past the threshold |
| `"summarization"` | trigger=30, keep=10 | Yes — LLM rollup in `Messages` | One-shot when count >> threshold |

### Truncation

Cheapest option. Drops the oldest entries beyond the recent window and writes one marker referencing them. No LLM call. Use when the model's recent-window memory is sufficient and pre-recent context is disposable.

### Sliding-window

Same shape as truncation but fires continuously — every turn that pushes the entry count past `WindowSize` triggers a small compaction. Amortizes compaction cost across many turns rather than bursting a large compaction occasionally.

### Summarization

Calls the agent's primary `IChatClient` (via `CompactionContext.ChatClient`) with a conservative summarization prompt + the messages from the target entries. The resulting summary text lands in the marker's `Messages` field, so subsequent inference sees `"summary of N earlier turns"` in place of the collapsed entries.

The default system prompt is intentionally domain-agnostic. For domain-specific summarization, register a custom strategy under the same key (`"summarization"`) — see [Custom Strategies](#custom-strategies).

---

## Custom Strategies

Register your own strategy via keyed DI. The Agents-side registration uses `TryAddKeyedSingleton`, so any custom registration under a built-in key wins:

```csharp
public sealed class MyDomainStrategy : ICompactionStrategy
{
    public IReadOnlyList<string>? EvaluateTrigger(IReadOnlyList<DurableSessionEntry> history)
    {
        // Return null to skip; return the source IDs to compact when triggered.
        if (history.Count < 50) return null;
        return history.Take(history.Count - 15)
                      .Where(e => e is not CompactionMarkerEntry)
                      .Select(e => e.CorrelationId)
                      .ToArray();
    }

    public async Task<CompactionResult> CompactAsync(
        CompactionContext context,
        CancellationToken cancellationToken = default)
    {
        // Build a marker. Use context.MarkerCorrelationId (workflow-supplied) so
        // activity retries are idempotent.
        return new CompactionResult
        {
            Marker = new CompactionMarkerEntry
            {
                CorrelationId = context.MarkerCorrelationId,
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = Array.Empty<ChatMessage>(),
                CompactedMessageIds = context.TargetMessageIds,
                Strategy = "my-domain",
                ModelId = string.Empty,
                OriginatingTurnCorrelationIds = context.TargetMessageIds,
            },
        };
    }
}

// Registration:
services.AddKeyedSingleton<ICompactionStrategy, MyDomainStrategy>("my-domain");

// Usage:
opts.AddDurableAgent("AgentX", a => { a.CompactionStrategyKey = "my-domain"; });
```

**Required contract:**
- `EvaluateTrigger` MUST be deterministic given the input history — it runs on the activity side after every turn and the workflow consumes the result.
- The marker's `CorrelationId` MUST be `context.MarkerCorrelationId` (workflow-supplied) so activity retries don't double-write.
- The marker's `CompactedMessageIds` SHOULD be a subset of `context.TargetMessageIds`. Other IDs would surface as `DurableCompactionMarkerException` on the next projected load.

---

## How It Works

```
┌─────────────────── AgentWorkflow ────────────────────┐
│                                                       │
│  for each turn:                                       │
│    stepResult = RunDurableAgentStep(...)              │
│      └─ strategy.EvaluateTrigger(rawHistory)          │
│         sets stepResult.CompactionNeeded + targets    │
│                                                       │
│    AppendAgentTurn(currentTurn)                       │
│                                                       │
│    if stepResult.CompactionNeeded:                    │
│      markerId = Workflow.NewGuid()  ◄── deterministic │
│      CompactHistory(targets, markerId)                │
│        └─ strategy.CompactAsync(...)                  │
│        └─ store.AppendAsync([marker])                 │
│                                                       │
└───────────────────────────────────────────────────────┘
```

Activity-side trigger evaluation (Q2 = B): the trigger predicate runs inside the chat activity (where the chat client + freshly-completed step state are accessible). The workflow consumes the resulting flag and dispatches `CompactHistory` after the current turn is appended.

The marker correlation ID is minted in the workflow via `Workflow.NewGuid()` so activity retries reproduce the same ID — preventing duplicate marker writes if `CompactHistory` is retried.

---

## Compaction + External History Store

**Compaction requires an external history store.** Markers live in the store; without one, `CompactHistory` throws an `InvalidOperationException` at first dispatch.

Configure your store as usual:
```csharp
opts.HistoryStore = sp => sp.GetRequiredService<IAgentHistoryStore>();
```

Your store implementation MUST honor the `applyCompaction` parameter on `LoadAsync`:
- `applyCompaction: false` → return raw entries, markers included as-is. Used by the `CompactHistory` activity itself + erasure cascades.
- `applyCompaction: true` → project markers in place. The Agents library uses this for inference-time loads (the LLM sees the projected view).

See [`docs/how-to/MAF/external-history-store.md`](./external-history-store.md) for the full interface contract. The reference test double `FakeAgentHistoryStore` and the sample `samples/MAF/Compaction/InMemoryCompactionAwareStore.cs` both demonstrate the marker-aware projection logic.

---

## Compaction + History Reducer

`HistoryReducer` and `UseCompaction` are complementary, not redundant.

| Layer | When | Operates on | Purpose |
|---|---|---|---|
| `UseCompaction` (Step 5+6) | After every final-step turn that crosses the trigger threshold | Audit canonical view (raw entries) | Bound in-session inference cost; produces markers |
| `HistoryReducer` (existing) | At continue-as-new only | Projected view (`applyCompaction: true`) | Bound workflow event-history size at CAN boundary |

Per the Q5α design rule, the reducer runs against the **post-compact projection** — so it operates on the view the LLM has been seeing rather than the raw entries. This means a session with both configured looks like:

1. Turns 1-30: history accumulates.
2. Turn 31: trigger fires, `CompactHistory` writes a marker subsuming turns 1-21 (default summarization config).
3. Turns 32+: each turn loads the projected view (marker + recent 10 + new turn).
4. Continue-as-new: `ReduceHistoryInStore` loads `applyCompaction: true` (projected view), runs the registered reducer, calls `ReplaceAsync`.

---

## GDPR Erasure Cascades

Erasure cascades correctly only if you route through `CompactionAwareErasureHelper`. Raw `ReplaceAsync` calls that delete source entries without rewriting markers will corrupt projection.

```csharp
using Temporalio.Extensions.Agents.HistoryStore;

var result = await CompactionAwareErasureHelper.EraseSessionDataAsync(
    store,
    sessionId,
    erasedMessageIds: new HashSet<string> { "msg-123", "msg-456" });

Console.WriteLine($"Markers affected: {result.MarkersAffected}");
Console.WriteLine($"Markers tombstoned: {result.MarkersTombstoned}");
Console.WriteLine($"Markers regenerated: {result.MarkersRegenerated}");
Console.WriteLine($"Remaining entries: {result.RemainingMessageCount}");
```

Behavior per marker:
- **Tombstone** (every source ID in the marker is in the erasure set) → remove the marker entirely.
- **Regenerate** (some but not all source IDs erased) → rewrite the marker with the surviving subset and `Messages` cleared (the rollup may reference erased content). Next compaction cycle produces a fresh summary.
- **Pass-through** (no overlap) → marker unchanged; only non-marker entries the erasure set names are dropped.

---

## Observability

The summarization strategy invokes the chat client inline within the `CompactHistory` activity, so the standard chat-client OpenTelemetry instrumentation already covers it:

- The activity emits its own span via Temporal's `TracingInterceptor` (`RunActivity:CompactHistory`).
- The chat client's `OpenTelemetryChatClient` (if installed) produces the `gen_ai.client.send` span.
- A dormant `RunCompactionSummary` activity exists in `AgentActivities` with span name `agent.compaction.summarize` and `gen_ai.usage.*` tags, available to custom strategies that want to dispatch summarization as a separately-tracked activity.

`CompactHistory` activity heartbeats are emitted on every progress step (load → strategy → append) so long-running summarizations stay alive under heartbeat-timeout pressure.

---

## Mixed-Version Replay

The `"compaction-marker"` polymorphic discriminator is registered in both `DurableAIJsonContext` and `AgentSessionJsonContext` (via the inline `[JsonDerivedType]` attribute on `DurableSessionEntry` plus per-context `[JsonSerializable]` entries).

A worker on an older build that pulls a workflow task whose history contains a marker raises `DurableReplayCompatibilityException` with `Discriminator == "compaction-marker"` rather than a vague `JsonException`. Upgrade the lagging worker.

The `tests/Temporalio.Extensions.AI.Tests/Compat/Snapshots/v0_3/discriminators.json` snapshot harness pins this contract — adding any new `[JsonDerivedType]` to `DurableSessionEntry` requires updating that snapshot.

---

## Limitations

- **Compaction is Agents-only at v0.4.0-preview.2.** The MEAI library (`Temporalio.Extensions.AI`'s `DurableChatWorkflow`) does not consume the trigger hook yet — the plan's Q13 commits MEAI compaction to a follow-up release.
- **No per-tool-call compaction.** Triggers fire only at end-of-turn (the `isFinal` step); mid-turn tool-call iterations are never compaction boundaries.
- **Strategies receive the entire raw history.** For sessions with hundreds of thousands of entries, the `EvaluateTrigger` call could become expensive. Custom strategies that want to bound this should subscribe to a separate signal (e.g. a count cached in their own state).
- **Marker re-compaction is not supported.** The built-in strategies skip pre-existing `CompactionMarkerEntry` entries when selecting compaction targets. Compacting a marker-of-markers would require strategy-specific merge logic not yet built.
- **The `RunCompactionSummary` activity is dormant.** Built-in `SummarizationCompactionStrategy` invokes the chat client inline within `CompactHistory` rather than dispatching a separate `RunCompactionSummary` activity. The activity is registered and tested; custom strategies that want a separately-tracked LLM activity can dispatch it manually.

---

## References

- **Sample**: `samples/MAF/Compaction/` — end-to-end driver with summarization + a compaction-aware in-memory store + GDPR erasure demo.
- **Design log**: `artifacts/maf-feature-gap-analysis.md` — Q2, Q5α, Q6, Q12, Q13 decisions.
- **API surface**:
  - `Temporalio.Extensions.Agents.Compaction.ICompactionStrategy`
  - `Temporalio.Extensions.Agents.Compaction.CompactionContext`
  - `Temporalio.Extensions.Agents.Compaction.CompactionResult`
  - `Temporalio.Extensions.Agents.Compaction.{Truncation,SlidingWindow,Summarization}CompactionStrategy`
  - `Temporalio.Extensions.AI.CompactionMarkerEntry`
  - `Temporalio.Extensions.AI.Exceptions.DurableCompactionMarkerException`
  - `Temporalio.Extensions.Agents.HistoryStore.CompactionAwareErasureHelper`
- **External history store**: [`docs/how-to/MAF/external-history-store.md`](./external-history-store.md)
- **History reducer + token optimization**: [`docs/how-to/MAF/prompt-caching.md`](./prompt-caching.md)
