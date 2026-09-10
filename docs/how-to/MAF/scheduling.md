# Scheduling Agent Runs

Use a Temporal Schedule for recurring stateless work, `ScheduleOneTimeAgentRunAsync` for a
one-time stateless job created by a workflow, and `RunAgentDelayedAsync` when the delayed work
needs a durable conversation session.

| Need | API | Execution model | Human approval |
|---|---|---|---|
| Recurring schedule declared with the worker | `AddScheduledAgentRun` | Stateless `AgentJobWorkflow` | Tool call is blocked |
| Recurring schedule created by the worker process | `ScheduleAgentAsync` | Stateless `AgentJobWorkflow` | Tool call is blocked |
| One-time job created inside a workflow | `ScheduleOneTimeAgentRunAsync` | Stateless `AgentJobWorkflow` | Tool call is blocked |
| One-time delayed conversation | `RunAgentDelayedAsync` | Stateful `AgentWorkflow` | Supported |

`AgentJobWorkflow` has no conversation history, persistent StateBag, approval wait, or typed
result. Each occurrence starts fresh. `AgentWorkflow` retains session history and StateBag and can
receive later messages.

## Register a recurring run

This is the canonical worker setup. Register the Temporal client explicitly, then add the worker,
the durable agent, and its schedule in one configuration block:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Api.Enums.V1;
using Temporalio.Client.Schedules;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Scheduling;

builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();

builder.Services
    .AddHostedTemporalWorker("agents-worker")
    .AddTemporalAgents(options =>
    {
        options.AddDurableAgent("DigestAgent", agent =>
        {
            agent.Instructions = "Summarize new customer feedback.";
            agent.ChatClient = services => services.GetRequiredService<IChatClient>();
        });

        options.AddScheduledAgentRun(
            agentName: "DigestAgent",
            scheduleId: "daily-digest",
            request: new RunRequest("Summarize feedback received since the previous digest."),
            spec: new ScheduleSpec
            {
                Calendars =
                [
                    new ScheduleCalendarSpec
                    {
                        Hour = [new ScheduleRange(8)],
                        Minute = [new ScheduleRange(0)],
                    },
                ],
                TimeZoneName = "America/New_York",
            },
            policy: new SchedulePolicy
            {
                Overlap = ScheduleOverlapPolicy.Skip,
                CatchupWindow = TimeSpan.FromMinutes(10),
            });
    });
```

Calendar schedules use UTC unless `TimeZoneName` is set. Choose an IANA time-zone name when the
schedule represents local wall-clock time and needs daylight-saving-time behavior.

At startup, configuration validation rejects an unknown agent name or a duplicate schedule ID.
If the schedule already exists in Temporal, the registration service logs a warning and leaves the
existing schedule unchanged. It does not reconcile code changes with server state.

### Overlap and catchup

`SchedulePolicy.Overlap` controls what Temporal does when the preceding occurrence is still
running. `Skip` is the SDK default and is usually safest for agents whose work should not overlap.

`CatchupWindow` applies when the Temporal Service could not create scheduled actions. Worker
downtime is different: the service can still start workflow executions, and their tasks wait until
a compatible worker is available.

Activities may execute more than once. Temporal makes the scheduling and workflow history durable,
but it cannot make an external side effect exactly once. Tools that send email, charge a card, or
write to another system still need an application idempotency key; configure a write tool with
`NoRetry()` when retrying it is unsafe.

## Create a recurring schedule at runtime

Resolve `ITemporalAgentClient` from the same process that registers the durable agent. The job input
contains the worker's per-agent tool, timeout, retry, and interceptor settings; a proxy-only client
does not have that configuration and is rejected instead of creating a partially configured job.

```csharp
var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

ScheduleHandle handle = await agentClient.ScheduleAgentAsync(
    agentName: "ReportAgent",
    scheduleId: "weekly-report",
    request: new RunRequest("Generate the weekly metrics report."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec
            {
                Hour = [new ScheduleRange(9)],
                DayOfWeek = [new ScheduleRange(1)],
            },
        ],
        TimeZoneName = "America/New_York",
    });
```

The handle controls the schedule entity, not the individual workflow result:

```csharp
await handle.TriggerAsync();
await handle.PauseAsync("Paused during maintenance.");
await handle.UnpauseAsync();

ScheduleHandle existing = agentClient.GetAgentScheduleHandle("weekly-report");
await existing.DeleteAsync();
```

### Update an existing schedule

Temporal schedules are mutable. `ScheduleHandle.UpdateAsync` receives the current description and
may invoke its callback more than once when updates conflict, so keep the callback deterministic
and free of side effects:

```csharp
var updatedSpec = new ScheduleSpec
{
    Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromHours(6))],
};

await handle.UpdateAsync(input =>
    new ScheduleUpdate(input.Description.Schedule with { Spec = updatedSpec }));
```

Config-time registration is create-only. Changing `AddScheduledAgentRun` does not update the
existing server schedule; a warning is logged. Apply the change with `UpdateAsync`, or delete the
schedule and restart the worker so startup registration creates it again.

The schedule action captures more than its timing. It also contains the request and resolved agent
execution settings. Changes to the prompt, tool policy, retry settings, timeouts, or iteration cap
do not alter an already-created schedule action. Update or recreate the schedule when those values
must change.

Schedules outlive workers and code registrations. Delete a schedule before decommissioning its
agent, or it will continue creating runs that cannot be serviced correctly.

## Schedule a one-time stateless job from a workflow

`ScheduleActivities.ScheduleOneTimeAgentRunAsync` starts one `AgentJobWorkflow` with Temporal's
`StartDelay`; it does not create a persistent Schedule entity. Invoke it as an activity because it
uses the Temporal client and wall-clock time.

Carry the baseline into the future request (or persist it under a stable application key). A
stateless job cannot read the originating agent session:

```csharp
using Temporalio.Workflows;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Scheduling;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string topic, string followupId)
    {
        var analyst = WorkflowAgents.GetTemporalAgent("AnalystAgent");
        var session = await analyst.CreateSessionAsync();
        var baseline = await analyst.RunAsync($"Analyze: {topic}", session);

        await Workflow.ExecuteActivityAsync(
            (ScheduleActivities activities) => activities.ScheduleOneTimeAgentRunAsync(
                new OneTimeAgentRun
                {
                    AgentName = "AnalystAgent",
                    RunId = followupId,
                    Request = new RunRequest(
                        $"Re-evaluate '{topic}' and compare it with this baseline:\n{baseline.Text}"),
                    RunAt = Workflow.UtcNow + TimeSpan.FromDays(7),
                }),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }
}
```

`RunId` is the idempotency key within the agent name. The library uses `UseExisting` while the
workflow is running and `RejectDuplicate` after it closes, so an activity retry cannot create a
second execution even if the first job completed before the activity result was recorded. Reusing
the same ID intentionally schedules nothing new.

If `RunAt` is in the past, the delay is clamped to zero. A per-run `RetryPolicy` overrides the
per-agent policy, then the worker policy; when all are absent, the library applies its bounded
five-attempt default.

## Start a delayed conversation

`RunAgentDelayedAsync` creates a full agent session now and defers its first workflow task. It
returns after Temporal accepts the request; it does **not** wait for, or return, the first agent
response.

```csharp
var sessionId = new TemporalAgentSessionId("OnboardingAgent", userId);

await agentClient.RunAgentDelayedAsync(
    sessionId,
    new RunRequest("Send the customer's scheduled onboarding check-in."),
    delay: TimeSpan.FromHours(24));
```

Use an idempotent tool or another application-owned completion channel to store or publish the
result. There is currently no high-level API that waits for the initial delayed response. Once your
application knows the work is finished and no follow-up messages are needed, release the session:

```csharp
await agentClient.ShutdownAsync(sessionId);
```

Do not call `RunAgentDelayedAsync` twice with the same session ID before its delay expires. A second
signal-with-start can dispatch the workflow early. If the session is already running, the request
is delivered to it and no new delay is applied.

## Output, approval, and data boundaries

### Capturing output

`AgentJobWorkflow` returns `Task`, not an agent response. Temporal Web shows operational execution
history, but it is not an application result API. Give the scheduled agent a durable tool or
activity that writes its output to an application store, keyed by the schedule occurrence or a
business identifier. That write must tolerate activity re-execution.

### Human approval

Stateless job workflows cannot park for review. `RequireApproval()` and interceptor decisions that
request approval are converted to blocked tool results; in-tool approval has no session context.
Use `RunAgentDelayedAsync` when the run must participate in the full approval protocol. See
[Human-in-the-loop patterns](./hitl-patterns.md) for the workflow-parked and in-tool models.

### Payloads and secrets

The schedule action stores the `RunRequest` and resolved execution settings in Temporal. One-time
workflow inputs are also recorded in workflow history. Do not place secrets in prompts or options,
and account for these payloads in retention, encryption, and access-control decisions.

## Operational reference

| Item | Behavior |
|---|---|
| Recurring workflow ID | `ta-{agent}-scheduled-{scheduleId}` is the configured base ID; each occurrence gets a distinct ID derived from it — read the real one from the schedule (see below) |
| One-time job workflow ID | `ta-{agent}-scheduled-{runId}` |
| `MaxToolCallsPerTurn` | Captured from the local durable-agent registration when the job is created |
| Activity timeout | Per-agent value, then worker default |
| One-time retry policy | Per-run value, then per-agent, then worker, then bounded default |
| `TemporalAgentContext.Current` | Unavailable to tools in `AgentJobWorkflow` |
| Schedule removal | Explicit; removing code registration does not delete server state |

### Getting a recurring occurrence's workflow ID

`ta-{agent}-scheduled-{scheduleId}` is the workflow ID the library configures on the schedule's
action. It is not the ID of any individual execution: the server derives a distinct per-occurrence
ID from that base so occurrences do not collide. How it derives that ID is a server implementation
detail, not a documented contract, so ask the schedule instead of building the string yourself:

```csharp
var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();
var temporalClient = host.Services.GetRequiredService<ITemporalClient>();

ScheduleHandle handle = agentClient.GetAgentScheduleHandle("weekly-report");
ScheduleDescription description = await handle.DescribeAsync();

foreach (ScheduleActionResult action in description.Info.RecentActions)
{
    if (action.Action is ScheduleActionExecutionStartWorkflow started)
    {
        // started.WorkflowId is the real workflow ID for this occurrence.
        WorkflowHandle occurrence = temporalClient.GetWorkflowHandle(
            started.WorkflowId,
            firstExecutionRunId: started.FirstExecutionRunId);
    }
}
```

Reconstructing the ID from `ScheduledAt` looks like it should work and does not. `ScheduledAt` is a
`DateTime` that carries sub-second precision (`...:08.6801730Z`), while the ID the server produced
for the same occurrence was truncated to whole seconds
(`ta-probeagent-scheduled-probe-wfid-schedule-2026-09-10T16:45:08Z`). A reconstructed string is off
by the fractional part and addresses a workflow that does not exist. Use
`ScheduleActionExecutionStartWorkflow.WorkflowId`; it is the ID the server actually used.

`Info.RunningActions` exposes in-flight occurrences the same way, as
`ScheduleActionExecution` values.

Scheduling emits `temporal.agent.schedule.create`, `temporal.agent.schedule.delayed`, and
`temporal.agent.schedule.one_time` spans. Once a run starts, `agent.turn` is emitted per model step.
See [Observability](./observability.md) for setup.

## References

- [Temporal Schedules](https://docs.temporal.io/schedule)
- [Temporal Activities and idempotency](https://docs.temporal.io/activities)
- [Human-in-the-loop patterns](./hitl-patterns.md)
- [Observability](./observability.md)
- [Do's and Don'ts](./dos-and-donts.md)

_Last updated: 2026-09-10_
