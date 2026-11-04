# ScheduledChat

A native Temporal Schedule starts `ScheduledTurnWorkflow`. Its application-owned activity calls
`DurableChatSessionClient.SendAsync` with an occurrence request ID derived from the schedule's ID
and nominal UTC scheduled time. No library scheduling API or provider credentials are needed.

## Run

Start Temporal Service 1.31.0+:

```bash
temporal server start-dev
```

Then run from the repository root:

```bash
dotnet run --project samples/MEAI/ScheduledChat
```

Set `TEMPORAL_ADDRESS` to use a different local endpoint (default `localhost:7233`, namespace
`default`). The sample uses a local `IChatClient` that returns a fixed status message, not a real
inference provider. It creates a unique paused hourly schedule, backfills two hourly occurrences
with `BufferAll`, waits for the turns, prints `Scheduled turns retained: 2`, deletes the schedule
and shuts down the chat session before stopping the worker.

For ongoing production operation, replace the demo model, use stable application-managed IDs,
unpause the schedule explicitly, and host the worker as a service. Schedule/workflow IDs and
plain-text search attributes must not contain secrets or sensitive user identifiers.

## Guarantees and limits

- Schedules start workflows; they cannot directly perform update-with-start. The trigger must
  await the submission activity's completed turn so overlap policy covers the full turn.
- Default `Skip` drops overlapping runs. `BufferOne` retains one queued run; additional overlaps
  are skipped. The explicit `BufferAll` backfill override processes the bounded two-run range.
- Activity retries reuse the occurrence ID and identical prompt. Session deduplication is bounded
  by its ledger and lineage; this is not exactly-once external-effect delivery.
- Repeating backfills can cost money or produce new effects after ledger eviction/session expiry.
  Separate schedules/manual producers can still receive `Busy` for the same session.
- Manual triggers with the same nominal time share an occurrence ID (the pinned server uses
  second-resolution manual times). Use a separate command identity for distinct manual actions.
- Three attempts, two minutes per attempt and five minutes overall bound submission. If a timeout
  leaves an admitted turn running, inspect/cancel it explicitly; stopping the worker is not session
  cancellation.
- The demo has no provider charges. Real providers charge for inference/tools separately from
  Temporal execution; backfills and retries can increase both.

For production capacity isolation, see the
[separate-queue operating guide](../../../docs/how-to/MEAI/operations.md). This sample deliberately
keeps model and tool work on its workflow queue; schedule overlap policy is not worker priority.

See the [scheduling guide](../../../docs/how-to/MEAI/scheduling.md) for configuration, costs,
backfill boundary behavior, privacy and replay-tested scenarios.
