# Compaction — in-session history compaction with summarization + GDPR erasure

Demonstrates [`UseCompaction`](../../../docs/how-to/MAF/compaction.md) end-to-end:

- A `SupportAgent` configured with `CompactionStrategyKey = "summarization"`.
- An `InMemoryCompactionAwareStore` that implements the full marker projection contract.
- 8 turns through the agent — enough to trip a low-threshold summarization strategy a couple of times.
- Audit canonical view vs. projected view side-by-side.
- A `CompactionAwareErasureHelper` cascade to show tombstone / regenerate behavior.

## Prerequisites

- A Temporal server running locally: `temporal server start-dev`
- An OpenAI API key configured via user secrets:

  ```bash
  dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/Compaction
  ```

## Run

```bash
dotnet run --project samples/MAF/Compaction/Compaction.csproj
```

## What you'll see

```
Worker started. Demonstrating in-session compaction.

Session: ta-supportagent-xxxxxxxxxxxxxxxx
Compaction strategy: "summarization" (trigger=6, keep=2 — fires after ~3 turns)

Turn 1: "I'm looking for a sci-fi novel — recommend one."
Agent : ...
        Store: 2 total (2 source + 0 marker)

Turn 2: "How much does it usually cost?"
Agent : ...
        Store: 4 total (4 source + 0 marker)

Turn 3: "What's the order ID format you use?"
Agent : ...
        Store: 6 total (6 source + 0 marker)

Turn 4: "Can I get free shipping?"
Agent : ...
        Store: 8 total (5 source + 1 marker)   ← compaction fired

... etc.

════════════════════════════════════════════════════════════════════
 View comparison
════════════════════════════════════════════════════════════════════

AUDIT CANONICAL (12 entries — applyCompaction: false):
  • marker-xxxxxxxxxxxx  MARKER (summarization, refs 5)
  • req-xxx              AgentSessionRequest
  • resp-xxx             AgentSessionResponse
  • ... etc.

PROJECTED (7 entries — applyCompaction: true, LLM-facing):
  • marker-xxxxxxxxxxxx  MARKER — "summary text the LLM sees instead of refs..."
  • req-xxx              AgentSessionRequest
  • ... etc.
```

The audit canonical view shows every entry that ever landed in the store — the marker AND all the source entries it subsumes. The projected view is what the LLM sees on the next turn: the marker stays (carrying the rollup summary) and the source entries it references drop out.

## How the trigger is tuned for the demo

`AddTemporalAgents` pre-registers a default `SummarizationCompactionStrategy` with thresholds `trigger=30, keep=10` — appropriate for production but too high to demonstrate in a short demo. The sample overrides the canonical `"summarization"` keyed-DI registration BEFORE calling `AddTemporalAgents`:

```csharp
builder.Services.AddKeyedSingleton<ICompactionStrategy>(
    SummarizationCompactionStrategy.Key,
    new SummarizationCompactionStrategy(
        triggerEntryCount: 6,   // each turn = 2 entries, so trigger after ~3 turns
        keepRecentCount: 2,
        systemPrompt: SummarizationCompactionStrategy.DefaultSystemPrompt));
```

The library's pre-registration uses `TryAddKeyedSingleton`, so any user-supplied registration under the same key wins.

## Compaction requires an external history store

Markers live in the store; without one, `CompactHistory` throws at first dispatch. The sample registers `InMemoryCompactionAwareStore` and wires `opts.HistoryStore = sp => sp.GetRequiredService<InMemoryCompactionAwareStore>()`.

`InMemoryCompactionAwareStore` is the reference implementation of the projection contract:

- `LoadAsync(applyCompaction: false)` → raw entries
- `LoadAsync(applyCompaction: true)` → markers stay (with their `Messages` summary); sources each marker references are filtered out
- `AppendAsync` uses `CorrelationId`-keyed dedupe so activity retries don't double-write
- Projection-validation: if a marker references a source the store no longer has, `DurableCompactionMarkerException` fires (Cypher mitigation #3 — surfacing orphan markers loudly rather than producing a silent misleading projection)

For production backends the same shape applies — only the IO layer differs.

## GDPR erasure cascade

The final section runs `CompactionAwareErasureHelper.EraseSessionDataAsync(store, sessionId, idsToErase)`. The helper:

1. Loads `applyCompaction: false` (audit canonical — markers + sources untouched)
2. For each marker, computes the intersection of `CompactedMessageIds` with the erasure set:
   - **All erased** → tombstone (drop the marker entirely)
   - **Some erased** → regenerate (rewrite with surviving subset + clear `Messages` so the summary cannot leak erased content)
   - **None overlap** → pass-through (marker unchanged)
3. Drops any non-marker entries in the erasure set
4. `ReplaceAsync`-es the store with the rewritten history
5. Returns `EraseResult { MarkersAffected, MarkersTombstoned, MarkersRegenerated, RemainingMessageCount }` for compliance reports

Never delete entries from the store directly when compaction is in play — markers would reference IDs the store no longer has, and the next `LoadAsync(applyCompaction: true)` would raise `DurableCompactionMarkerException`.

## References

- [`docs/how-to/MAF/compaction.md`](../../../docs/how-to/MAF/compaction.md) — full how-to
- [`docs/how-to/MAF/external-history-store.md`](../../../docs/how-to/MAF/external-history-store.md) — store interface contract
- [`docs/how-to/MAF/prompt-caching.md`](../../../docs/how-to/MAF/prompt-caching.md) — `HistoryReducer` × `UseCompaction` precedence
