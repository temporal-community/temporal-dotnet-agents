# Scheduled durable chat turns

Use Temporal's native Schedule API. A schedule **starts a short trigger workflow**; that workflow
awaits an application-owned activity which calls `DurableChatSessionClient.SendAsync`. A schedule
cannot directly execute an update or update-with-start, and `SendAsync` is not a workflow-safe API.
Do not schedule the long-lived `DurableChatWorkflow` itself: starting a session does not submit a turn.
No library scheduler abstraction is needed.

The runnable [ScheduledChat sample](../../../samples/MEAI/ScheduledChat/) uses a local demo
`IChatClient`, so it needs no provider credentials. It runs on the repository's existing Temporal
Service 1.31.0+ baseline (tested with Server 1.31.2 and SDK 1.21.1).

## Create the schedule

Register `ScheduledTurnWorkflow` and `ScheduledTurnActivities` on an application worker alongside
`AddDurableAI`. The activity needs a resolvable `DurableChatSessionClient`; use a root-resolvable
`ITemporalClient` configured with `DurableAIDataConverter.Instance`. In split deployments, configure
session-start policies on `AddDurableAIClient`, not only on the inference worker.

The types in this example are application code from the sample, not library APIs. Supply
`client`, opaque `scheduleId` and `conversationId`, and `taskQueue` from your application:

```csharp
var schedule = await client.CreateScheduleAsync(scheduleId, new Schedule(
    ScheduleActionStartWorkflow.Create(
        (ScheduledTurnWorkflow workflow) => workflow.RunAsync(
            new ScheduledTurnInput(conversationId, "Report the service status.")),
        new WorkflowOptions($"trigger-{scheduleId}", taskQueue)),
    new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(TimeSpan.FromHours(1))] })
{
    Policy = new SchedulePolicy
    {
        Overlap = ScheduleOverlapPolicy.Skip,
        CatchupWindow = TimeSpan.FromMinutes(10),
        PauseOnFailure = true,
    },
    State = new ScheduleState { Paused = true },
});
```

<!-- Compiled in tests/docs/DocSnippets/Snippets/MEAI/Scheduling.cs. -->

The example starts **paused** to avoid unbounded recurring work. Backfill and manual triggers can
still run while paused. For production, explicitly unpause after checking the next action times,
worker availability, cost limits, and authorization. A schedule persists when its creating process
or worker stops; pause or delete it explicitly when no longer needed. Deletion does not cancel
already-started trigger workflows or the chat session.

## Occurrence identity and retry boundaries

The [trigger workflow](../../../samples/MEAI/ScheduledChat/ScheduledTurnWorkflow.cs) reads
`TemporalScheduledById` and `TemporalScheduledStartTime` from Temporal's built-in typed search
attributes. It builds `scheduled/{scheduleId}/{scheduledUtcTicks}` and passes it, the fixed prompt,
and the conversation ID to the submission activity. These built-in attributes require no custom
search-attribute registration. Missing occurrence metadata fails nonretryably before submission.

The ID uses the **nominal scheduled instant**, including for buffered and backfilled actions,
not the time the activity happens to execute, the retry attempt, a random GUID, or the workflow
run ID. Keep schedule IDs short and opaque. Two manual triggers sharing a nominal timestamp are
the same logical occurrence in this sample; on the pinned server manual trigger timestamps have
second resolution. Use a separate application command identity if distinct same-instant manual
invocations are required.

The [submission activity](../../../samples/MEAI/ScheduledChat/ScheduledTurnActivities.cs) awaits
the **completed turn**, not fire-and-forget admission. If its acknowledgment is lost after the
turn succeeds, retrying uses the same request ID and identical input. The bounded session ledger
can return the recorded result without another model call. Retrying a failed or cancelled turn
does not create a fresh logical turn. A changed prompt or other effect-bearing input under that
ID is a conflict, not a way to amend a previous occurrence.

This is bounded deduplication, **not exactly-once execution**: ledger eviction, session expiry or
restart, and uncertain external tool effects remain relevant. Replaying a range after the session
lineage or ledger has expired can create new effects. Use an application-owned durable occurrence
store and downstream idempotency enforcement if duplicates must be prevented beyond that window.
Keep schedule payloads/configuration stable when replaying an occurrence.

The sample bounds submission to three attempts, two minutes per attempt and five minutes overall.
These limits are for a short status check, not human approval waits. Align them with your model,
tool and approval deadlines. Cancelling a trigger/activity RPC wait does **not** reliably cancel an
already-admitted turn; use the session's explicit cancellation API and inspect its terminal outcome.

## Overlap and backfill

| Policy | Behavior while a previous trigger is still awaiting its turn |
|---|---|
| `Skip` | Drop overlapping occurrences; favor fresh periodic status over catch-up. |
| `BufferOne` | Keep at most one waiting occurrence, then run it after the active trigger completes; additional overlaps are skipped, not accumulated. |
| `BufferAll` | Queue every occurrence sequentially. Use deliberately for bounded backfills, not as an unbounded cost-control default. |

Overlap is **per schedule**, not per conversation or across schedules. Manual callers, another
schedule, or an activity timeout can still overlap the same session and receive `Busy`. Bounded
activity retries may absorb temporary contention; exhausted retries fail the trigger and
`PauseOnFailure` pauses subsequent automatic actions. Overlap controls do not replace the session's
admission rules or serialize work from other producers.

The sample deliberately backfills two hourly occurrences, even though its default policy is
`Skip`, using an explicit `BufferAll` override:

```csharp
var now = DateTime.UtcNow;
var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
await schedule.BackfillAsync(
    [new ScheduleBackfill(end.AddHours(-2).AddSeconds(1), end, ScheduleOverlapPolicy.BufferAll)]);
```

<!-- Compiled in tests/docs/DocSnippets/Snippets/MEAI/Scheduling.cs. -->

Use UTC ranges and inspect their matching instants before large backfills. The start above is
deliberately off the hourly boundary: on the pinned server an exactly matching start boundary was
included, despite the SDK XML description of an exclusive start. Integration coverage checks that
this range produces exactly the intended two occurrences and that repeating it retains two turns.
`CatchupWindow` controls automatic catch-up after server outages; it is not a cap on an explicit
backfill.

## Cost, privacy, and operations

Each executed occurrence adds a trigger workflow and submission activity, plus session updates,
model-step activities and any tool activities. Temporal execution/event charges depend on your
deployment; provider token/tool charges are separate. Retries and backfills multiply work, and
`BufferOne` limits backlog rather than total spend. Set model-context budgets, finite retries,
catch-up limits and alerts; start paused and use a bounded range to estimate cost. The demo model
incurs no provider charges, but still exercises real Temporal execution.

Prompts and session history persist in Temporal payloads. Keep identifiers opaque; do not put
prompts, customer identifiers or secrets in schedule/workflow IDs, search attributes, or metric
labels. Content-capture opt-out does not encrypt persisted payloads; use a
[payload codec](payload-codecs.md) when required.

`ScheduledChatSampleTests` executes real server schedules for `Skip`, `BufferOne`, repeated
backfills, and a submission failure **after** success but before acknowledgment. It checks model
invocation counts and retained results, then replays trigger and session histories. The sample
deletes its schedule and shuts down its session on completion.
