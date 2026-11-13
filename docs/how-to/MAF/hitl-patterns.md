# Human-in-the-loop patterns

Some things an agent wants to do should not happen until a person says yes: a refund over a
threshold, a force-push to a shared branch, anything that spends money or is hard to undo.

There are two ways to build that pause. The choice comes down to one question about your own
design:

> **Are you approving the tool call, or a step inside it?**

If a person decides **whether this tool runs at all**, park the workflow. The tool never starts.
The workflow records the request and waits durably. No tool activity is running during the review,
although Temporal still retains the workflow state and history; the configured wait can last days.

If a person decides **something the tool discovered halfway through its own work** — "the migration
found 400 rows to delete, continue?" — the tool activity stays in flight while it waits, using
worker activity capacity and continuing to heartbeat. If the worker loses that activity, the
workflow-side approval remains pending and needs a decision, cancellation, or expiry.

**Start with workflow-parked.** Not just because it survives a deploy — three sharper reasons are
in [What usually decides it](#what-usually-decides-it).

> **New to Temporal?** An *activity* is the unit of real work Temporal runs on one of your worker
> processes. Once its completion is recorded, workflow replay reuses that result. The activity can
> still run more than once before completion is recorded, so effectful work must tolerate retries or
> disable them. A *workflow* is the durable coordinator deciding which activities run; its state
> survives process restarts. "Parking" the workflow means it stops and waits with nothing running.

Approval routing and application authorization are separate concerns. Follow the normative
[security boundary](../../security.md) for reviewer endpoints and effectful tools.

---

## What usually decides it

Three facts remove the choice more often than taste does.

**1. During an in-tool review, the rest of the turn runs.**

A model response can request several tools at once. Workflow-parked clears every approval in that
batch before dispatching *any* tool activity — so if the model asks for `apply_refund` and
`send_refund_confirmation` together, the confirmation cannot go out while the refund is under
review.

MAF dispatches tool activities serially in model order. With in-tool approval, the current tool
activity remains open while the reviewer reads the request, and later calls in the same response
wait; they are not dispatched concurrently.

**2. In-tool approval keeps its activity open.**

In-tool approval ties up one activity for the duration of human review. Calls in the same model
response are dispatched serially, so the next tool does not start until the current approval
finishes. Workflow-parked approvals still stop before any tool activity starts.

**3. The shipped defaults are wrong for in-tool, and nothing warns you.**

| Option | Default |
|---|---|
| `DefaultActivityTimeout` | **5 minutes** |
| `DefaultHeartbeatTimeout` | 2 minutes |
| `DefaultApprovalTimeout` | **7 days** |

For workflow-parked those are fine — no activity is open, so a seven-day window is reasonable. For
in-tool it is a trap: the tool activity can time out after five minutes while the workflow holds the
approval for up to seven days. Nothing validates these budgets at startup. Set `ApprovalTimeout`
below the tool's effective start-to-close timeout, leaving time for work before and after the
approval wait. The tool inherits the agent `ActivityTimeout`, then worker `DefaultActivityTimeout`,
unless its `StartToCloseTimeout` is overridden with `WithTimeout()`. `HeartbeatTimeout` controls
heartbeat expiry; it does not extend the activity's start-to-close limit.

### The differences that matter

| | Workflow-parked | In-tool |
|---|---|---|
| What the human approves | Whether the tool runs | A step inside a running tool |
| Running during the review | No tool activity | The tool activity remains in flight on a worker; heartbeat pumps continue |
| Resumes after worker restart / deploy | **Yes** | **No** — the workflow keeps the pending approval, but the activity does not resume |
| Sibling tools in the same turn | Held until all approvals settle | Run serially; later calls wait |
| More than one approval per turn | Queued in call order | Processed one at a time |
| Realistic review window | Hours to days | Minutes |
| Extra configuration | None — defaults suit it | Activity and approval budgets must align; heartbeat timeout is optional |
| Who owns the `RequestId` | The workflow, after the interceptor returns | The tool, before it asks |
| Declared by | `RequireApproval()`, or an interceptor's `PauseForApproval(...)` | `RequestApprovalAsync` in the tool body |

**Both shapes block the caller.** A turn is a single workflow update, and the approval happens
inside it, so whoever called `SendAsync` waits for the human either way. Workflow-parked frees the
*worker*, not the caller. See [Driving the turn while it waits](#driving-the-turn-while-it-waits).

---

## Scenarios

**Refunds over $500 need a manager.** Managers work business hours; a Friday evening request waits
until Monday. → **Workflow-parked, via an interceptor.** The threshold is a property of the call's
arguments, not the tool, so `RequireApproval()` would gate the $12 refunds too. Nothing runs over
the weekend, and the 7-day default covers it with no configuration.

**A coding agent must ask before force-pushing.** → **Workflow-parked, via `RequireApproval()`.**
Every force-push needs its own review, so the gate belongs on the tool and no interceptor is needed.

**Support escalations sit in a queue overnight.** → **Workflow-parked** — and note that turns within
one session are serialized. A distinct request while that turn is active is rejected as busy;
matching retries can join only within the bounded joiner capacity. Scope your session to the *case*,
not the user, or the user cannot keep chatting.

**A migration tool discovers mid-run that it would delete 400 rows.** → **In-tool** when that
approval-relevant result is available only inside the tool's execution. If an interceptor can
reliably perform the needed preflight instead, workflow-parked review remains an option. Budget
enough time for the whole tool activity, make it the only approval in the turn, and account for
worker loss leaving the workflow approval pending.

**Not sure yet.** → **Workflow-parked.** It is the cheaper mistake: worst case you restructure a
tool later, instead of shipping a stranded approval.

Before reaching for in-tool because "the reviewer needs richer information", note that an
interceptor runs as a real Temporal activity and can do I/O — look up the customer, price the
refund, fetch the policy — and put the result in the approval description. Enriching the prompt is
not by itself a reason to keep an activity open.

---

## Workflow-parked approval

The workflow records a `DurableApprovalRequest`, waits durably, and **schedules no tool activity**
until the reviewer decides or the approval timeout expires.

```csharp
options.AddDurableAgent("Operations", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddTool(sendEmail, tool => tool.RequireApproval());
});
```

Tool activities have one attempt by default. A failed activity can still leave the external effect's
outcome uncertain; use downstream idempotency and reconcile before retrying the write. The approval
gate has already resolved before the tool activity is scheduled.

To decide per call rather than per tool, register an interceptor returning
`DurableToolDecision.PauseForApproval(...)` — see [tool-interceptor.md](./tool-interceptor.md). The
interceptor is also where you author what the reviewer sees, and the natural place to notify them.

---

## In-tool approval

### Budget the approval and activity timeouts first

```csharp
opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(20);  // default start-to-close budget for the tool
opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(15);  // leaves five minutes for other tool work

// Optional: change the inherited 2-minute heartbeat timeout.
opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);   // RequestApprovalAsync also heartbeats at ~1/3

agent.AddTool(publishDraft);
```

The approval wait and tool invocation share the tool activity's start-to-close budget. Keep
`ApprovalTimeout` below the effective `StartToCloseTimeout` with room for work before and after the
wait; it defaults from the agent's `ActivityTimeout` and worker's `DefaultActivityTimeout`, and a
per-tool `WithTimeout()` can override it. The example's 20-minute activity budget is not a guarantee
that 15 minutes is enough if the tool does substantial work outside the approval wait.

Tool activities use one attempt by default. An explicit agent retry policy also applies to this
tool, so only opt into retries when the activity and approval protocol are safe to repeat.

**Heartbeats are on by default.** Every package-managed tool activity receives an effective
heartbeat timeout (2 minutes unless overridden). The activity wrapper sends empty heartbeats at
intervals of `min(1 second, half the heartbeat timeout)` with a 1 ms minimum, or every second if no
positive timeout is configured. With a positive timeout, `RequestApprovalAsync` also sends
heartbeats at about one-third of it while waiting. Heartbeats do not include streamed assistant text
or tool output, and there is no supported option to disable the package pumps. The activity's
start-to-close timeout remains the overall limit.

**Heartbeating is not durability.** The wait is resident on one worker for its entire duration. A
deploy, crash, or scale-down ends it — heartbeats or not. A failed activity can still leave the
effect's outcome uncertain; reconcile before retrying an external write.

### The tool body

```csharp
static async Task<string> PublishDraftAsync(string draft)
{
    var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            // Guid.NewGuid() is fine here — a tool body runs in an activity, not in workflow
            // code. Never do this inside a [Workflow] method.
            RequestId    = Guid.NewGuid().ToString("N"),
            FunctionName = "publish_draft",
            Description  = "Publish this draft?",
        });

    return decision.Approved ? "Published" : $"Not published: {decision.Reason}";
}
```

Unlike workflow-parked, the tool owns the `RequestId` *before* the request exists — so it can write
your reviews-table row and send a deep link, then ask. That is in-tool's one genuine convenience;
the example relies on the default one-attempt tool policy. If you enable agent-wide retries, use a
persisted, idempotent request identity rather than generating a new ID on every attempt.
`TemporalAgentContext` does not expose the originating model tool-call ID. Leave `CallId` unset, or
set it only to an application-owned correlation value that the tool already knows; do not add a
`callId` tool parameter and trust the model to supply it.

---

## Reviewer-side concerns

This is the part most teams underestimate, and it applies to both shapes.

### Finding pending approvals

**There is no `ITemporalAgentClient` API that lists sessions awaiting review**, and the built-in
agent search attributes do not mark pending approvals. `GetPendingApprovalAsync` answers for one
session whose ID you already hold.

So your application owns the queue. For workflow-parked approval, register an interceptor and write
the session ID and reviewer-safe details from its activity. A bare `RequireApproval()` without an
interceptor still parks the workflow but does not publish an application queue record. For in-tool
approval, write the record from the tool before calling `RequestApprovalAsync`. Have your reviewer
console read that application-owned record; the library answers "is *this* session waiting?", never
"which sessions are waiting?".

### Driving the turn while it waits

`SendAsync` does not return until the turn finishes, and a turn parked for approval does not finish
until a human decides. There are two dispatch choices and one hard rule:

- **Interactive review** — start the turn without awaiting, poll, resolve, then await.
- **Queued dispatch** — `RunAgentFireAndForgetAsync` starts the turn with no caller attached, but
  supply and persist a `RequestId`, then use `GetTurnOutcomeAsync(sessionId, requestId)` to query
  its pending or retained outcome, including response details when retained. Outcomes are bounded;
  `Unavailable` does not prove the request was never accepted. The fire-and-forget proxy returns an
  empty `AgentResponse` and does not update the local session; this API does not expose full session
  history, so persist results separately if your application needs them beyond the retained outcome.
- **Never** hold an inbound HTTP request open across a review window.

```csharp
var session = (TemporalAgentSession)await proxy.CreateSessionAsync(cancellationToken);
var sessionId = session.SessionId;
var turn = proxy.RunAsync("Refund order ORD-001", session);

// Keep servicing approvals until the turn finishes. One turn can raise several, one at a
// time, so exiting after the first would leave the next one hanging until it times out.
while (!turn.IsCompleted)
{
    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

    DurableApprovalRequest? pending;
    try
    {
        pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
    }
    catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
    {
        continue;   // the workflow may not exist yet on the first poll
    }

    if (pending is null) continue;

    var resolution = await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            CallId = pending.CallId,
            FunctionName = pending.FunctionName,
            Approved  = true,
            Reason    = "Reviewed under ticket INC-1234.",
        },
        cancellationToken);

    if (resolution.Status is not DurableApprovalResolutionStatus.Accepted)
    {
        throw new InvalidOperationException(
            $"Approval was not accepted: {resolution.Status}.");
    }
}

var response = await turn;
```

When the pending request has a tool-call `CallId`, the decision must match both `CallId` and
`FunctionName`. In-tool requests may have no `CallId`; then `RequestId` is required, and
`FunctionName` is optional but must match if supplied. Mismatched identity is rejected before
acceptance.

`GetPendingApprovalAsync` is a workflow query: it does not block or add workflow history. Poll at an
interval appropriate to your application's load.

Only a matching pending decision is accepted. Unknown, mismatched, already-resolved, or conflicting
decisions fail update validation before Temporal records them. After an ambiguous RPC result, inspect
the pending request and reconcile through application-owned review state; do not assume retrying
the update will return the original result. The result/status types are defined in
[Durable approvals](../../concepts/durable-approvals.md#one-call-decisions).

### What the reviewer can be shown

| Field | Workflow-parked | In-tool |
|---|---|---|
| `RequestId` | Generated by the workflow | You supply it |
| `FunctionName`, `CallId` | Filled from the tool call | `FunctionName` is yours; `CallId` is `null` unless you supply an application-owned value |
| `Description` | Interceptor's enriched description, then message, then `"Approve invocation of tool '{name}'"` | Whatever you pass |
| `ReviewData` | Interceptor metadata only; otherwise `null` | Whatever you pass |
| `ExpiresAt` | Workflow sets *now + `ApprovalTimeout`*; history reserve can expire it sooner | Same |

**A bare `RequireApproval()` with no interceptor produces an approval screen that reads "Approve
invocation of tool 'issue_refund'" and shows no amount.** Authoring the description and metadata is
the work; it is the difference between a demo and a product.

`ReviewData` is `null` unless explicitly supplied; the workflow-parked request still has its
interceptor-derived or default description. The combined description and metadata are limited to 64
metadata entries and 16 KiB of serialized JSON. Oversized requests fail with the non-retryable
`DurableApprovalReviewProjectionTooLarge` error before the workflow accepts them, including requests
made through the in-tool `RequestApprovalAsync` API.

Raw model arguments are never copied into `ReviewData` automatically — that is a deliberate
injection-surface decision. Treat `Description` as model-influenced and sanitize it for the reviewer.

### What the model sees when a call does not run

Workflow-parked decisions become ordinary tool results, so the turn continues and the model usually
narrates the outcome:

| Workflow-parked outcome | Result content |
|---|---|
| Denied, timed out, or expired at the history reserve | `[Denied] {reason}` — timeout reasons name the elapsed window |
| Blocked by an interceptor | `[Blocked] {message}` |

The model is not told a human was involved unless your reason text says so. "Nobody looked at it"
and "a human said no" are indistinguishable to the agent unless the reviewer supplies a
distinguishing reason — so always send one.

In-tool approval is different: `RequestApprovalAsync` returns a `DurableApprovalDecision` to your
tool. The tool decides whether to return a result, throw, or take another action. In the example
above, the model receives `Not published: {reason}`, not the workflow-parked `[Denied]` format.

### When the activity dies mid-review

If an in-tool activity ends mid-review, the tool is gone but the workflow-side approval is not.

- **After the failed turn finishes, later non-approval turns can run.** Pending approval alone does
  not block admission; a distinct request can still be rejected as busy while another turn is active.
- **The next turn needing a human fails** with `DurableApprovalAlreadyPending`. The session keeps
  working right up until it next needs a human, then jams.
- **It clears on its own — eventually.** The abandoned request resolves as a rejection when
  `ApprovalTimeout` elapses or the workflow history reserve is reached. At the 7-day default that
  is not a recovery plan; at fifteen minutes it may be. Inspect event-history pressure with
  [`GetHistoryBudgetAsync`](./usage.md#inspecting-workflow-history-budget).
- **It delays continue-as-new and shutdown** rather than being lost — both wait for in-flight
  handlers.

To recover immediately:

```csharp
try
{
    // No-op when no approval is pending; a concurrent resolution may race this cancellation.
    await client.CancelPendingApprovalAsync(sessionId, "Reviewer went away.", cancellationToken);
}
catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
{
    // The session workflow never started — nothing to cancel.
}
```

---

## Approval in regular workflow orchestration

Approval state is owned by the registered `AgentWorkflow`. A regular workflow that needs an
approval-capable agent turn should invoke the registered proxy through an activity, passing
workflow-generated session and request IDs. There is no workflow-local agent execution API.

---

## Reference

- `ApprovalTimeout` bounds the durable wait and resolves a timeout **as a rejection**, with a reason
  naming the elapsed window. Reaching the workflow history reserve can reject it sooner.
- Cancellation propagates as cancellation, never as a denial.
- Approvals from one model response are raised **sequentially** in call order, each with its own full
  `ApprovalTimeout` — worst case for a batch is *N × ApprovalTimeout*, and no tool in the batch runs
  until all of them settle.
- Turns within a session are serialized: a parked approval blocks other turns in that session.
- The latest 32 resolutions are retained across continue-as-new, bounding how long a reviewer's retry
  stays deduplicated.

## Samples

| Sample | Shows |
|---|---|
| `samples/MAF/HumanInTheLoop` | In-tool approval with a sized timeout budget — interactive, excluded from the sample canary |
| `samples/MAF/ToolInterceptor` | Workflow-parked policy, reviewer-safe metadata, and the poll-while-running pattern |

See also [Durable approvals](../../concepts/durable-approvals.md),
[Tool interceptor](tool-interceptor.md), and the MEAI equivalent,
[MEAI HITL patterns](../MEAI/hitl-patterns.md).
