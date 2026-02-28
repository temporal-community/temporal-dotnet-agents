# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.1.0] - 2026-02-28

### Added

#### Scheduling infrastructure

- **`AgentJobWorkflow`** — a new internal Temporal workflow for scheduled and deferred agent
  runs. Unlike `AgentWorkflow`, it carries no conversation history, no `StateBag`, no TTL loop,
  and no `[WorkflowUpdate]` handlers. It executes a single `AgentActivities.ExecuteAgentAsync`
  call and exits. Workflow ID convention: `ta-{agentName}-scheduled-{scheduleId}`.

- **`AgentJobInput`** — internal input record for `AgentJobWorkflow`. Carries `AgentName`,
  `TaskQueue`, `Request`, and optional `ActivityStartToCloseTimeout` / `ActivityHeartbeatTimeout`
  overrides.

#### Recurring Temporal Schedules (`ITemporalAgentClient`)

- **`ITemporalAgentClient.ScheduleAgentAsync`** — creates a Temporal Schedule that fires
  `AgentJobWorkflow` on a caller-supplied `ScheduleSpec`. 

- **`ITemporalAgentClient.GetAgentScheduleHandle`** — retrieves an existing `ScheduleHandle`
  by schedule ID for out-of-band lifecycle operations (e.g., decommissioning an agent's
  schedule without restarting the worker).

#### Config-time Schedule Registration (`TemporalAgentsOptions`)

- **`TemporalAgentsOptions.AddScheduledAgentRun`** — declares a recurring scheduled run at
  configuration time. Runs are registered with Temporal automatically on worker startup via a
  new `ScheduleRegistrationService` hosted service. Startup is idempotent: if a schedule
  already exists (e.g., on subsequent worker restarts), a warning is logged and creation is
  skipped rather than overwriting the existing schedule.

- **`ScheduleRegistrationService`** — internal `IHostedService` that calls
  `ITemporalAgentClient.ScheduleAgentAsync` for every run declared via `AddScheduledAgentRun`.
  Catches `ScheduleAlreadyRunningException` and logs a warning instead of throwing.

#### Deferred One-Time Runs from Inside Workflows (`ScheduleActivities`)

- **`ScheduleActivities`** — new public activity class for use inside orchestrating workflows.
  Contains a single `[Activity]`-decorated method:

  - **`ScheduleOneTimeAgentRunAsync(OneTimeAgentRun run)`** — schedules a future, one-time
    `AgentJobWorkflow` run using `WorkflowOptions.StartDelay`. Uses
    `WorkflowIdConflictPolicy.UseExisting` for idempotency on activity retry. If `RunAt` is
    in the past when the activity executes, the run starts immediately (delay clamped to zero).

- **`OneTimeAgentRun`** — public record describing a deferred one-time run: `AgentName`,
  `RunId` (used to build the deterministic workflow ID), `Request`, and `RunAt`.

#### Deferred Session Start (`ITemporalAgentClient`)

- **`ITemporalAgentClient.RunAgentDelayedAsync`** — starts an agent session workflow with a
  `StartDelay`, so execution is deferred by the specified `TimeSpan`. The workflow is created
  immediately in Temporal but does not begin executing until the delay elapses. If a workflow
  with the same session ID is already running (`UseExisting` policy), the delay is ignored and
  the existing workflow is reused.

- **`TemporalAIAgentProxy.RunDelayedAsync`** (internal) — surfaces `RunAgentDelayedAsync`
  on the proxy for callers that hold a `TemporalAIAgentProxy` reference directly.

#### Registration changes (`TemporalWorkerBuilderExtensions.AddTemporalAgents`)

- Registers `AgentJobWorkflow` alongside `AgentWorkflow` on the worker.
- Pre-registers `ScheduleActivities` as a singleton (factory closes over `taskQueue`) before
  calling `AddSingletonActivities<ScheduleActivities>()`, so the task-queue binding is correct.
- Conditionally registers `ScheduleRegistrationService` as an `IHostedService` when at least
  one scheduled run has been declared via `AddScheduledAgentRun`.

### Known Limitations

- **Schedule orphaning**: Temporal Schedules are independent of workers. Removing an agent from
  `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. Use
  `GetAgentScheduleHandle` to retrieve the handle and call `DeleteAsync()` when decommissioning.

- **Config drift in `AddScheduledAgentRun`**: if a schedule's spec changes in code (e.g., from
  daily to twice-daily), the change is silently ignored on restart because the existing schedule
  is skipped. To apply the update, delete the schedule first via `GetAgentScheduleHandle`, then
  restart the worker.

- **`RunAgentDelayedAsync` delay ignored for existing sessions**: `StartDelay` only applies when
  starting a brand-new session. If a workflow with the same session ID is already running, the
  delay is ignored and the existing workflow is reused immediately.

- **Scheduled run results are not captured**: scheduled runs are fire-and-forget by design.
  Run status and workflow event history are visible in the Temporal Web UI.

[0.1.0]: https://github.com/cecilphillip/TemporalAgents/releases/tag/v0.1.0
