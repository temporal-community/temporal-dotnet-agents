# Scheduling Agent Runs

How to schedule recurring and one-time agent runs — from config-time registration to programmatic schedule management and deferred workflows.

---

## Table of Contents

1. [Overview](#overview)
2. [Why scheduling is different here](#why-scheduling-is-different-here)
3. [Two Workflow Types](#two-workflow-types)
4. [Recurring Schedules](#recurring-schedules)
5. [One-Time Deferred Runs](#one-time-deferred-runs)
6. [Schedule Lifecycle Management](#schedule-lifecycle-management)
7. [Graceful Shutdown](#graceful-shutdown)
8. [Workflow ID Conventions](#workflow-id-conventions)
9. [Observability](#observability)
10. [Pitfalls and Gotchas](#pitfalls-and-gotchas)
11. [Choosing the Right Primitive](#choosing-the-right-primitive)

---

## Overview

TemporalAgents provides four scheduling primitives, each suited to a different context:

| Primitive | Context | Recurrence | Workflow Type |
|-----------|---------|------------|---------------|
| `AddScheduledAgentRun` | Config time | Recurring | `AgentJobWorkflow` |
| `ITemporalAgentClient.ScheduleAgentAsync` | Runtime (external) | Recurring | `AgentJobWorkflow` |
| `ScheduleActivities.ScheduleOneTimeAgentRunAsync` | Inside a workflow | One-time | `AgentJobWorkflow` |
| `ITemporalAgentClient.RunAgentDelayedAsync` | Runtime (external) | One-time | `AgentWorkflow` |

The first three use `AgentJobWorkflow` — a lightweight, fire-and-forget workflow. The fourth uses the full `AgentWorkflow` with conversation history and StateBag.

---

## Why scheduling is different here

Building recurring or deferred agent runs on a non-durable substrate means assembling several pieces yourself: a cron system (Celery beat, AWS EventBridge, a Kubernetes `CronJob`) to trigger execution on schedule, a queue to absorb runs and hand them off to workers, idempotency keys in a database to prevent double-execution when a worker retries, and manual crash-recovery logic for runs that die mid-execution. Each piece is manageable in isolation. The combination — keeping them consistent, handling missed fires during downtime, making a failed run observable after the fact — is where things quietly break.

Here, none of that is hand-wired. Temporal Schedules store their state in the server, not in your infrastructure: they survive worker restarts, catch up on missed fires according to the policy you set, and appear in the Web UI without any extra instrumentation. Workflow timers handle one-time deferral with the same guarantee — the timer fires exactly once and requires no polling loop on your side. Because each scheduled run is a full Temporal workflow, activities retry automatically, the run is visible in the UI, and a worker crash mid-run resumes from where it left off rather than silently dropping the job.

What you would otherwise spread across three or four systems is a single method call. The payoff is not just fewer moving parts — it is that scheduling and agent execution share the same durability contract.

---

## Two Workflow Types

Understanding the distinction between these two workflows is key to choosing the right scheduling approach.

### AgentJobWorkflow (Scheduled/Deferred)

A minimal workflow that drives the same durable-agent dispatch loop as `AgentWorkflow`, but without long-lived session state:

```csharp
// Internal — you don't instantiate this directly.
// Summarized for documentation; see AgentJobWorkflow.cs for the full source.
[Workflow("TemporalCommunity.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
        var stepActivityOptions = new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout    = input.HeartbeatTimeout,
            RetryPolicy         = input.RetryPolicy,
        };

        var accumulated = new List<ChatMessage>(input.Request.Messages);

        for (var iteration = 0; iteration < input.MaxToolCallsPerTurn; iteration++)
        {
            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(...),
                stepActivityOptions);

            accumulated.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null or { Count: 0 })
                return;

            // Each tool call is fanned out in parallel as InvokeAgentTool activities
            // via Workflow.WhenAllAsync. Per-tool DurableToolOptions apply identically
            // to interactive sessions.
        }
    }
}
```

**Properties:**
- No conversation history — starts fresh every time (no `HistoryStore` load on `IsFirstStep`)
- No StateBag persistence (`SerializedStateBag` is always `null`)
- No external history store integration. The `HistoryStore` factory on `TemporalAgentsOptions` or `DurableAgentBuilder` is ignored for `AgentJobWorkflow`. Neither `LoadAsync` nor `AppendAsync` is called. If your worker has a store configured, scheduled runs are not written to it.
- No TTL loop or `[WorkflowUpdate]` handlers
- No continue-as-new
- Result is visible in the Temporal Web UI event history
- Same per-step / per-tool activity dispatch as the long-lived `AgentWorkflow`, so retries, timeouts, and per-tool `DurableToolOptions` apply identically

> **`MaxToolCallsPerTurn` propagation:** The iteration cap set on `DurableAgentBuilder.MaxToolCallsPerTurn` is read by `ScheduleAgentAsync` and stored in `AgentJobInput.MaxToolCallsPerTurn` before the workflow starts. You do not need to configure it separately for scheduled runs — if you set `agent.MaxToolCallsPerTurn = 5` on the agent definition, that cap applies in both session-based and scheduled runs. The default is `20` when not set.

### AgentWorkflow (Full Session)

The standard long-lived workflow with conversation history, StateBag, HITL, and continue-as-new. Only `RunAgentDelayedAsync` uses this for scheduling, because it creates a full session that can receive follow-up messages after the initial delayed run.

---

## Recurring Schedules

### Config-Time Registration

Declare scheduled runs inside `AddTemporalAgents`. The `ScheduleRegistrationService` (a `BackgroundService`) creates them automatically when the worker starts.

The following example sets up a `DigestAgent` that summarizes new customer feedback every day at 08:00:

```csharp
using Microsoft.Extensions.AI;
using Temporalio.Client.Schedules;
using TemporalCommunity.Extensions.Agents;

builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents-worker")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("DigestAgent", agent =>
        {
            agent.Instructions = "You summarize new customer feedback into a concise daily digest.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddScheduledAgentRun(
            agentName:  "DigestAgent",
            scheduleId: "daily-digest",
            request:    new RunRequest("Summarize all new customer feedback since yesterday."),
            spec: new ScheduleSpec
            {
                Calendars =
                [
                    new ScheduleCalendarSpec
                    {
                        Hour      = [new ScheduleRange(8)],
                        Minute    = [new ScheduleRange(0)],
                    }
                ]
            });
    });
```

**What happens on worker restart:** If the schedule already exists (e.g., from a previous startup), a `ScheduleAlreadyRunningException` is caught, a warning is logged, and the existing schedule is left untouched. The worker does **not** overwrite or update the schedule.

### Programmatic Scheduling

Call `ScheduleAgentAsync` at any time to create a Temporal Schedule. Resolve `ITemporalAgentClient` from DI and pass a `ScheduleSpec`. The example below creates a weekly report that fires every Monday at 09:00:

```csharp
using Temporalio.Client.Schedules;
using TemporalCommunity.Extensions.Agents;
using Microsoft.Extensions.DependencyInjection;

// ITemporalAgentClient is registered automatically when using AddTemporalAgents.
var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

var handle = await agentClient.ScheduleAgentAsync(
    agentName:  "ReportAgent",
    scheduleId: "weekly-metrics-report",
    request:    new RunRequest("Generate the weekly metrics report and post it to #reports."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec
            {
                Hour      = [new ScheduleRange(9)],
                DayOfWeek = [new ScheduleRange(1)],  // 1 = Monday
            }
        ]
    });

Console.WriteLine($"Schedule created. Use handle to pause/trigger/delete.");
```

### Schedule Policy

Both registration methods accept an optional `SchedulePolicy` for overlap and catchup behavior. This is useful when a run may still be executing when the next tick fires, or when a worker was down and you need to control how many missed runs are caught up:

```csharp
opts.AddScheduledAgentRun(
    agentName:  "InventoryAgent",
    scheduleId: "hourly-inventory-sync",
    request:    new RunRequest("Sync inventory levels from the warehouse API."),
    spec: new ScheduleSpec
    {
        Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromHours(1))]
    },
    policy: new SchedulePolicy
    {
        Overlap        = ScheduleOverlapPolicy.Skip,         // skip if previous run is still active
        CatchupWindow  = TimeSpan.FromMinutes(10)            // catch up missed runs within 10 min
    });
```

---

## One-Time Deferred Runs

### From Inside a Workflow

Use `ScheduleActivities.ScheduleOneTimeAgentRunAsync` to schedule a future run from inside an orchestrating workflow. This uses Temporal's `StartDelay` — a single workflow execution is created with a delayed start, leaving no persistent schedule entity behind once it completes.

The example below runs an analysis immediately, then schedules a follow-up comparison in 7 days:

```csharp
using Temporalio.Activities;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Scheduling;
using Temporalio.Workflows;
using static TemporalCommunity.Extensions.Agents.TemporalWorkflowExtensions;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string topic)
    {
        // Run the initial analysis now.
        var analyst = GetAgent("AnalystAgent");
        var session = await analyst.CreateSessionAsync();
        await analyst.RunAsync($"Analyze the current state of: {topic}", session);

        // Schedule a follow-up in 7 days — dispatched as an activity so it
        // uses the Temporal client from DI and is idempotent on activity retry.
        await Workflow.ExecuteActivityAsync(
            (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(
                new OneTimeAgentRun
                {
                    AgentName = "AnalystAgent",
                    RunId     = $"followup-{topic.ToLowerInvariant().Replace(" ", "-")}",
                    Request   = new RunRequest(
                        $"Compare today's findings on '{topic}' against the baseline from 7 days ago."),
                    RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7),
                }),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }
}
```

**Idempotency:** If the activity retries after a crash-before-ack, `WorkflowIdConflictPolicy.UseExisting` ensures the second `StartWorkflowAsync` call finds the already-scheduled workflow and returns normally.

**Past `RunAt`:** If `RunAt` is in the past when the activity executes, the delay is clamped to zero and the run starts immediately.

### From an External Caller

`RunAgentDelayedAsync` defers the start of a **full `AgentWorkflow` session** — with conversation history and StateBag. Use this when you need a delayed session that can still receive follow-up messages after the initial run.

Internally, this uses signal-with-start: the workflow is created and the initial request signal are delivered to Temporal in a single atomic RPC. This prevents a crash window between workflow creation and message delivery.

The example below creates a trial-welcome session that fires 24 hours after signup:

```csharp
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Microsoft.Extensions.DependencyInjection;

var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

// Session is created immediately but starts executing after 24 hours.
var sessionId = new TemporalAgentSessionId("OnboardingAgent", userId);

await agentClient.RunAgentDelayedAsync(
    sessionId,
    new RunRequest("Welcome! Your trial period has started. How can I help you get set up?"),
    delay: TimeSpan.FromHours(24));

// Once the delay elapses and the agent responds, you can send follow-up messages
// to the same session using the same session ID:
//
//   await agentClient.RunAgentAsync(
//       sessionId,
//       new RunRequest("How is the setup going? Do you need help with anything?"));
```

> **Duplicate-call behavior within the delay window:** If `RunAgentDelayedAsync` is called a second time with the same session ID before the delay elapses, the second `SignalWithStart` call delivers the signal to the not-yet-started workflow — causing it to start immediately, ahead of its scheduled delay. Do not call this method twice for the same session before the delay expires.

> **Already-running session:** If a workflow with the same session ID is already running (`UseExisting` conflict policy), the new request signal is delivered to the running workflow. No new workflow is started and no delay is applied.

---

## Schedule Lifecycle Management

The `ScheduleHandle` returned by `ScheduleAgentAsync` (or retrieved via `GetAgentScheduleHandle`) provides full lifecycle control:

```csharp
using Temporalio.Client.Schedules;
using TemporalCommunity.Extensions.Agents;
using Microsoft.Extensions.DependencyInjection;

var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

// Create a recurring schedule.
var handle = await agentClient.ScheduleAgentAsync(
    agentName:  "ReportAgent",
    scheduleId: "weekly-metrics-report",
    request:    new RunRequest("Generate the weekly metrics report."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec
            {
                Hour      = [new ScheduleRange(9)],
                DayOfWeek = [new ScheduleRange(1)],
            }
        ]
    });

// Trigger immediately outside the normal cadence (e.g., to validate the agent).
await handle.TriggerAsync();

// Pause for a planned maintenance window.
await handle.PauseAsync(note: "Pausing during data migration.");

// Resume when the window closes.
await handle.UnpauseAsync();

// Retrieve an existing handle from a different service or process.
var existing = agentClient.GetAgentScheduleHandle("weekly-metrics-report");

// Delete when decommissioning the schedule.
await existing.DeleteAsync();
```

### Updating a Schedule's Spec

Temporal schedules are immutable once created via `ScheduleRegistrationService`. To apply a changed spec:

1. Delete the existing schedule
2. Either restart the worker (so `ScheduleRegistrationService` recreates it) or call `ScheduleAgentAsync` with the new spec directly

```csharp
// Step 1: Delete the old schedule.
var handle = agentClient.GetAgentScheduleHandle("daily-digest");
await handle.DeleteAsync();

// Step 2: Create with the updated spec.
await agentClient.ScheduleAgentAsync(
    agentName:  "DigestAgent",
    scheduleId: "daily-digest",
    request:    new RunRequest("Summarize all new customer feedback since yesterday."),
    spec: new ScheduleSpec
    {
        // Changed: twice daily instead of once.
        Calendars =
        [
            new ScheduleCalendarSpec { Hour = [new ScheduleRange(8)] },
            new ScheduleCalendarSpec { Hour = [new ScheduleRange(20)] },
        ]
    });
```

Alternatively, delete via the Temporal CLI before restarting the worker:

```bash
temporal schedule delete --schedule-id daily-digest
```

---

## Graceful Shutdown

`ITemporalAgentClient.ShutdownAsync` sends a graceful shutdown signal to a running `AgentWorkflow` session, causing it to exit the session loop rather than sitting parked until its `TimeToLive` expires. This does not affect `AgentJobWorkflow` runs — those complete naturally when the agent's response is final.

The primary use case for scheduled work is a delayed full session: call `ShutdownAsync` after the agent has finished its work and you have no further messages to send.

```csharp
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;
using Microsoft.Extensions.DependencyInjection;

var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();
var sessionId   = new TemporalAgentSessionId("OnboardingAgent", userId);

// Wait for the delayed session to complete its first turn.
var response = await agentClient.RunAgentAsync(
    sessionId,
    new RunRequest("Trial check-in: what features have you tried so far?"));

Console.WriteLine(response.Text);

// No more messages expected — shut the session down immediately rather than
// waiting for the 14-day TimeToLive to expire.
await agentClient.ShutdownAsync(sessionId);
```

---

## Workflow ID Conventions

Scheduled and deferred runs use a distinct naming convention to avoid collisions with interactive sessions:

| Context | Workflow ID Format | Example |
|---------|-------------------|---------|
| Interactive session | `ta-{agent}-{key}` | `ta-onboardingagent-user-42` |
| Scheduled/deferred run | `ta-{agent}-scheduled-{id}` | `ta-reportagent-scheduled-weekly-metrics-report` |

The `-scheduled-` infix ensures that a recurring schedule never accidentally targets an existing interactive session, and vice versa. Temporal appends a timestamp automatically for recurring schedules (e.g., `ta-reportagent-scheduled-weekly-metrics-report-2026-06-04T09:00:00Z`).

---

## Observability

Three OTel spans cover scheduling operations:

| Span | Emitted By | Key Attributes |
|------|-----------|---------------|
| `temporal.agent.schedule.create` | `ScheduleAgentAsync` | `agent.name`, `schedule.id` |
| `temporal.agent.schedule.delayed` | `RunAgentDelayedAsync` | `agent.name`, `agent.session_id`, `schedule.delay` |
| `temporal.agent.schedule.one_time` | `ScheduleOneTimeAgentRunAsync` | `agent.name`, `schedule.job_id`, `schedule.delay` |

Once the scheduled workflow executes, the standard `agent.turn` span fires inside `AgentActivities.RunDurableAgentStepAsync` — the same code path as interactive sessions, with one span per LLM call. This means scheduled runs are fully visible in your tracing backend alongside interactive sessions.

For full OTel setup instructions, see [Observability](./observability.md).

---

## Pitfalls and Gotchas

### Schedule Orphaning

Temporal Schedules are **independent of workers**. Removing an agent from `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. The scheduled workflow will fail with `AgentNotRegisteredException` on each trigger.

**Always** delete the schedule before decommissioning an agent:

```csharp
var handle = agentClient.GetAgentScheduleHandle("daily-digest");
await handle.DeleteAsync();
```

### Config Drift

If you change a schedule's spec in code (e.g., from daily to hourly), the change is **silently skipped** on restart — `ScheduleRegistrationService` catches the `ScheduleAlreadyRunningException` and logs a warning. The old spec remains active.

**Fix:** Delete the schedule first, then restart:

```bash
# Via Temporal CLI
temporal schedule delete --schedule-id daily-digest
```

Or programmatically:

```csharp
await agentClient.GetAgentScheduleHandle("daily-digest").DeleteAsync();
```

### Duplicate Delayed Sessions

`RunAgentDelayedAsync` uses `WorkflowIdConflictPolicy.UseExisting`. If the session workflow is already running, the request is delivered to the running workflow as a signal — no new workflow is started and no delay is applied. This is by design, but it can be surprising if you expect the delay to apply unconditionally.

Additionally, a second `RunAgentDelayedAsync` call for the same session ID _before_ the delay window expires will cause the workflow to start immediately, bypassing the original delay. Avoid scheduling the same session twice before the delay expires.

### Activity Timeouts for Scheduled Runs

`AgentJobWorkflow` inherits `ActivityTimeout` and `HeartbeatTimeout` from `TemporalAgentsOptions` (via the per-agent override, then the worker default). If your scheduled agent makes long-running tool calls, ensure the timeout is sufficient:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents-worker")
    .AddTemporalAgents(opts =>
    {
        opts.DefaultActivityTimeout = TimeSpan.FromMinutes(60);

        opts.AddDurableAgent("ReportAgent", agent =>
        {
            agent.ActivityTimeout = TimeSpan.FromMinutes(90); // per-agent override
            agent.ChatClient      = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

There is no per-schedule timeout override — the effective per-agent timeout (or worker default) is used.

---

## Choosing the Right Primitive

**Is the schedule known at deploy time?**
- **Yes** → Use `AddScheduledAgentRun` for zero-code schedule management

**Does the schedule need to be created dynamically (e.g., user-triggered)?**
- **Yes, recurring** → Use `ScheduleAgentAsync`
- **Yes, one-time from outside a workflow** → Use `RunAgentDelayedAsync`
- **Yes, one-time from inside a workflow** → Use `ScheduleOneTimeAgentRunAsync`

**Does the scheduled run need conversation history?**
- **Yes** → Use `RunAgentDelayedAsync` (creates a full `AgentWorkflow` session)
- **No** → Use any of the other three (all use the stateless `AgentJobWorkflow`)

**Do you need to send follow-up messages after the delayed run?**
- **Yes** → Use `RunAgentDelayedAsync` — the session persists and accepts further messages
- **No** → Use `ScheduleOneTimeAgentRunAsync` or `AddScheduledAgentRun`

**Do you want to release session resources immediately after the run?**
- **Yes** → Call `ShutdownAsync` after the final message exchange rather than waiting for `TimeToLive` to expire

---

## References

- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentJobWorkflow.cs` — fire-and-forget workflow for scheduled runs
- `src/TemporalCommunity.Extensions.Agents/Workflows/ScheduleActivities.cs` — one-time scheduling from inside workflows
- `src/TemporalCommunity.Extensions.Agents/Workflows/ScheduleRegistrationService.cs` — config-time schedule creation
- `src/TemporalCommunity.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — `ScheduleAgentAsync` and `RunAgentDelayedAsync`
- `src/TemporalCommunity.Extensions.Agents/ITemporalAgentClient.cs` — `ShutdownAsync` and full interface surface
- `src/TemporalCommunity.Extensions.Agents/Workflows/ScheduleAgentRegistration.cs` — internal registration record
- [Usage Guide](./usage.md) — `AddDurableAgent` registration patterns
- [Observability](./observability.md) — scheduling OTel spans
- [Temporal Schedules Documentation](https://docs.temporal.io/workflows#schedule)

---

_Last updated: 2026-06-04_
