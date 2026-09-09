# Human-in-the-loop patterns

Some things an agent wants to do should not happen until a person says yes: a refund over a
threshold, a force-push to a shared branch, anything that spends money or is hard to undo.

There are two ways to build that pause. The choice comes down to one question about your own
design:

> **Are you approving the tool call, or a step inside it?**

If a person decides **whether this tool runs at all**, park the workflow. The tool never starts.
The workflow records the request, stops, and waits — nothing is running anywhere, so a deploy or a
crash during the review costs nothing, and the wait can last days.

If a person decides **something the tool discovered halfway through its own work** — "the migration
found 400 rows to delete, continue?" — the tool has to be running in order to ask, so it stays
running while it waits. That works, and it costs you everything in the paragraph above.

**Start with workflow-parked.** Not just because it survives a deploy — three sharper reasons are
in [What usually decides it](#what-usually-decides-it).

> **New to Temporal?** An *activity* is the unit of real work Temporal runs on one of your worker
> processes; its result is recorded, so completed work is never redone. A *workflow* is the durable
> coordinator deciding which activities run; its state survives process restarts. "Parking" the
> workflow means it stops and waits with nothing running.

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

In-tool does not get that protection. From the workflow's point of view an in-tool tool is an
ordinary tool: it is dispatched with its siblings, and they execute while your reviewer reads the
request. This, not deploy safety, is the strongest argument for the default.

**2. Only one approval can be outstanding at a time.**

Two in-tool approvals in one model response collide. Both activities are dispatched together, both
raise a request, and the second fails with `DurableApprovalAlreadyPending`. Workflow-parked raises
approvals strictly one at a time in tool-call order, so several in one turn queue cleanly.

**3. The shipped defaults are wrong for in-tool, and nothing warns you.**

| Option | Default |
|---|---|
| `DefaultActivityTimeout` | **5 minutes** |
| `DefaultHeartbeatTimeout` | 2 minutes |
| `DefaultApprovalTimeout` | **7 days** |

For workflow-parked those are fine — no activity is open, so a seven-day window is reasonable. For
in-tool it is a trap: the tool activity dies after five minutes while the workflow holds the
approval for another six days. Nothing validates the relationship at startup. Every in-tool
approval must set all three together.

### The differences that matter

| | Workflow-parked | In-tool |
|---|---|---|
| What the human approves | Whether the tool runs | A step inside a running tool |
| Running during the review | Nothing | The tool's activity, on one worker, doing no work |
| Survives worker restart / deploy | **Yes** | **No** |
| Sibling tools in the same turn | Held until all approvals settle | **Run concurrently** |
| More than one approval per turn | Queued in call order | **Fails** — one pending at a time |
| Realistic review window | Hours to days | Minutes |
| Extra configuration | None — defaults suit it | Three timeouts, all must change |
| Reusable session grants (`ScopeAware()`) | Available | Not available |
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
Every force-push needs review, so the gate belongs on the tool and no interceptor is needed. If a
developer is watching and approving many similar writes, add `ScopeAware()` so one decision can
cover a shape of call.

**Support escalations sit in a queue overnight.** → **Workflow-parked** — and note that turns within
one session are serialized. While an escalation waits, that session accepts no other turns. Scope
your session to the *case*, not the user, or the user cannot keep chatting.

**A migration tool discovers mid-run that it would delete 400 rows.** → **In-tool.** The
approval-relevant fact does not exist until the tool has done the analysis; neither the tool
registration nor the interceptor can show a number nobody has computed yet. Budget minutes, make it
the only approval in the turn, and accept that a deploy ends the review.

**A nightly compliance sweep wants to quarantine accounts.** → **Neither.** Scheduled jobs cannot
park for review at all — see [Where approval does not work](#where-approval-does-not-work). Have the
job *propose* into your own store and drive the quarantine through a managed session.

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
    agent.AddTool(sendEmail, tool => tool.NoRetry().RequireApproval());
});
```

`NoRetry()` matters here: an approved send that fails to report success would otherwise be retried,
re-entering the gate and potentially sending twice.

To decide per call rather than per tool, register an interceptor returning
`DurableToolDecision.PauseForApproval(...)` — see [tool-interceptor.md](./tool-interceptor.md). The
interceptor is also where you author what the reviewer sees, and the natural place to notify them.

---

## In-tool approval

### Budget the three timeouts first

```csharp
opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(20);  // outer bound on the whole review
opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);   // pump runs at a third of this
opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(15);  // must stay under ActivityTimeout

agent.AddTool(publishDraft, tool => tool.NoRetry());
```

**`ApprovalTimeout` must be smaller than `ActivityTimeout`.** Otherwise the activity dies first and
leaves the workflow holding an approval nobody is waiting on.

**`NoRetry()` is a correctness requirement here, not only an idempotency one.** On retry the tool
issues a *fresh* approval request while the first is still pending, so every attempt after the first
fails with `DurableApprovalAlreadyPending`.

**The heartbeat pump is on by default.** Every tool activity inherits a heartbeat timeout (2 minutes
unless overridden), and the package heartbeats at exactly a third of it for the whole wait. You lose
the pump only by setting `HeartbeatTimeout` to `TimeSpan.Zero`. The hazard to watch is
`ActivityTimeout`, not a missing heartbeat.

**Heartbeating is not durability.** The wait is resident on one worker for its entire duration. A
deploy, crash, or scale-down ends it — heartbeats or not — and with `NoRetry()` on a write tool that
ends the turn.

### The tool body

```csharp
static async Task<string> PublishDraftAsync(string draft, string callId)
{
    var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            // Guid.NewGuid() is fine here — a tool body runs in an activity, not in workflow
            // code. Never do this inside a [Workflow] method.
            RequestId    = Guid.NewGuid().ToString("N"),
            FunctionName = "publish_draft",   // set these, or the reviewer sees an
            CallId       = callId,            // approval card with no idea what it is for
            Description  = "Publish this draft?",
        });

    return decision.Approved ? "Published" : $"Not published: {decision.Reason}";
}
```

Unlike workflow-parked, the tool owns the `RequestId` *before* the request exists — so it can write
your reviews-table row and send a deep link, then ask. That is in-tool's one genuine convenience.

---

## Reviewer-side concerns

This is the part most teams underestimate, and it applies to both shapes.

### Finding pending approvals

**There is no API that lists sessions awaiting review**, and no search attribute marks one — the
library registers only `AgentName`, `SessionCreatedAt`, and `TurnCount`. `GetPendingApprovalAsync`
answers for one session whose ID you already hold.

So your application owns the queue. Write the session ID and reviewer-safe details to your own store
when the approval is raised — from the interceptor, which is already an activity and can do I/O —
and have your reviewer console read that. The library answers "is *this* session waiting?", never
"which sessions are waiting?".

### Driving the turn while it waits

`SendAsync` does not return until the turn finishes, and a turn parked for approval does not finish
until a human decides. Three ways to handle it:

- **Interactive review** — start the turn without awaiting, poll, resolve, then await.
- **Queued review** — use `RunAgentFireAndForgetAsync`. The turn runs with no caller attached and
  you read the result from session history later. This is the right shape for a review SLA measured
  in hours.
- **Never** hold an inbound HTTP request open across a review window.

```csharp
var sessionId = new TemporalAgentSessionId("Operations", sessionKey);
var turn = proxy.RunAsync("Refund order ORD-001", session);

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

    await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved  = true,
            Reason    = "Reviewed under ticket INC-1234.",
        },
        cancellationToken);
}

var response = await turn;
```

`GetPendingApprovalAsync` is a workflow query: it never blocks and adds no history, so poll freely.

Resolution is retry-safe, but the equivalence check compares **both** `Approved` and `Reason` as
exact strings — resubmitting the same decision with re-typed reason text returns `Conflict`, not
`AlreadyResolved`. Five statuses exist (`Accepted`, `AlreadyResolved`, `NotPending`,
`RequestMismatch`, `Conflict`); a reviewer UI should handle all of them. See
[Durable approvals](../../concepts/durable-approvals.md).

### What the reviewer can be shown

| Field | Workflow-parked | In-tool |
|---|---|---|
| `RequestId` | Generated by the workflow | You supply it |
| `FunctionName`, `CallId` | Filled from the tool call | **`null` unless you set them** |
| `Description` | Interceptor's enriched description, else `"Approve invocation of tool '{name}'"` | Whatever you pass |
| `ReviewData` | Interceptor metadata only | Whatever you pass |
| `ExpiresAt` | Overwritten by the workflow to *now + `ApprovalTimeout`* — render a real countdown | Same |

**A bare `RequireApproval()` with no interceptor produces an approval screen that reads "Approve
invocation of tool 'issue_refund'" and shows no amount.** Authoring the description and metadata is
the work; it is the difference between a demo and a product.

Raw model arguments are never copied into `ReviewData` automatically — that is a deliberate
injection-surface decision. **This hygiene does not extend to `Description`:** the built-in
interceptor installed by `UseApprovalScopes()` formats the raw tool arguments into the description
string. Treat `Description` as model-influenced and scrub it in your own interceptor if that matters.

### What the model sees when a call does not run

Every refusal arrives as an ordinary tool result, so the turn continues and the model usually
narrates the outcome:

| Outcome | Result content |
|---|---|
| Denied, or approval timed out | `[Denied] {reason}` — the timeout reason names the elapsed window |
| Blocked by an interceptor | `[Blocked] {message}` |

The model is not told a human was involved unless your reason text says so. "Nobody looked at it"
and "a human said no" are indistinguishable to the agent unless the reviewer supplies a
distinguishing reason — so always send one.

### When the activity dies mid-review

If an in-tool activity ends mid-review, the tool is gone but the workflow-side approval is not.

- **Ordinary turns still work.** The run validator never consults pending approvals.
- **The next turn needing a human fails** with `DurableApprovalAlreadyPending`. The session keeps
  working right up until it next needs a human, then jams.
- **It clears on its own — eventually.** The abandoned request resolves as a rejection when
  `ApprovalTimeout` elapses. At the 7-day default that is not a recovery plan; at fifteen minutes it
  may be.
- **It delays continue-as-new and shutdown** rather than being lost — both wait for in-flight
  handlers.

To recover immediately:

```csharp
try
{
    // No-op when nothing is pending; ignores an already-resolved request.
    await client.CancelPendingApprovalAsync(sessionId, "Reviewer went away.", cancellationToken);
}
catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
{
    // The session workflow never started — nothing to cancel.
}
```

---

## Reusable session grants

By default one decision covers one call. With grants a reviewer approves a *shape* of call —
`write_file` under `/tmp/*` — and matching calls later in the **same session** proceed without
another prompt until the grant expires.

```csharp
agent.AddTool(writeFile, tool => tool.NoRetry().RequireApproval().ScopeAware());
agent.UseApprovalScopes();   // installs the built-in scope-aware interceptor
```

Three things to know first:

**`UseApprovalScopes()` and `AddToolInterceptor()` are mutually exclusive.** Calling both throws
`InvalidOperationException` in either order. Approval scopes install their own interceptor and remove
scope-aware tools from the unconditional `RequireApproval` floor, so replacing that interceptor would
silently bypass their gate. Pick one per agent.

**Granting a scope also approves the call in front of you.** `GrantSessionScopeAsync` resolves the
pending request as approved *and* records the grant — call it **instead of** `ResolveApprovalAsync`.

**Grants are workflow-parked only**, session-local, always expiring, and bounded (256 records /
32 KiB by default, tunable). They expire on workflow time, survive continue-as-new, and never cross
sessions. Permanent and cross-session grants are intentionally unsupported.

The administrative service registration, the grant request contract, and the revoke path are covered
in [Durable approvals](../../concepts/durable-approvals.md). It is a separate registration on
purpose: keep it behind an authenticated backend.

**Approval is not effect-time authorization.** A tool that changes an external system must re-read
current tenant, ownership, and authorization state immediately before performing the effect —
especially after a long wait. `Actor`, `Reason`, descriptions, and review data are untrusted data,
not authentication evidence.

---

## Where approval does not work

Two execution paths cannot park for external review, because neither host workflow carries the
approval state machine:

- **Workflow-local sub-agents** — `WorkflowAgents.GetTemporalAgent(...)` inside your own workflow.
- **Scheduled jobs** — runs hosted by `AgentJobWorkflow`.

On both, `RequireApproval()` and `PauseForApproval()` **degrade to Block**: the tool does not run and
the model receives a synthetic blocked result. In-tool `TemporalAgentContext.Current` throws, naming
which path you are on.

If work on those paths needs review, own the approval in your orchestrating workflow, or drive the
agent through a managed session via `TemporalAIAgentProxy`.

---

## Reference

- `ApprovalTimeout` bounds the durable wait and resolves a timeout **as a rejection**, with a reason
  naming the elapsed window.
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
| `samples/MAF/ApprovalScopes` | One-call decisions plus expiring session grants — interactive |

See also [Durable approvals](../../concepts/durable-approvals.md),
[Tool interceptor](tool-interceptor.md), and the MEAI equivalent,
[MEAI HITL patterns](../MEAI/hitl-patterns.md).
