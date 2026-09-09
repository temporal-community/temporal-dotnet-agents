# Human-in-the-loop patterns

Two approval shapes, and the choice between them is about **durability, not duration**.

| | Workflow-parked | In-tool |
|---|---|---|
| Who waits | The workflow | An open activity, on one worker |
| Survives worker restart / deploy | **Yes** | **No** |
| Tool activity while waiting | Not scheduled yet | Running, doing nothing |
| Declared by | `RequireApproval()` or an interceptor | `RequestApprovalAsync` inside the tool |

Reach for workflow-parked by default. Reach for in-tool only when the tool itself must decide
*where* in its own logic the prompt happens — and then keep the window short.

Approval routing and application authorization are separate concerns. Follow the normative
[security boundary](../../security.md) for reviewer endpoints and effectful tools.

---

## Workflow-parked approval

The workflow records a `DurableApprovalRequest`, waits durably, and **schedules no tool activity
at all** until the reviewer decides or the approval timeout expires. Nothing is running, so a
deploy, crash, or scale-down during the review costs nothing.

```csharp
options.AddDurableAgent("Operations", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddTool(sendEmail, tool => tool.NoRetry().RequireApproval());
});
```

An interceptor can decide per call instead of per tool, by returning
`DurableToolDecision.PauseForApproval(...)`. See [tool-interceptor.md](./tool-interceptor.md).

Every call in one model-produced batch clears interceptor evaluation and approval *before* any
tool activity is scheduled. An allowed sibling therefore cannot execute while another call in that
batch is still awaiting review.

Resolve from outside with the typed client:

```csharp
var pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
if (pending is not null)
{
    var result = await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved  = true,
            Reason    = "Reviewed under ticket INC-1234.",
        },
        cancellationToken);
}
```

Resolution is retry-safe: an identical retry returns `AlreadyResolved`, a retry that changes the
decision returns `Conflict`. Reviewer-facing `ReviewData` comes only from interceptor-authored
metadata — raw model arguments are never copied into it automatically.

---

## In-tool approval

The tool calls `TemporalAgentContext.Current.RequestApprovalAsync`. The workflow still records the
request durably, but **the tool activity stays open for the whole review**.

```csharp
static async Task<string> PublishDraftAsync(string draft)
{
    var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = "Publish this draft?",
        });

    return decision.Approved ? "Published" : "Not published";
}
```

Three things this shape demands:

**Set a heartbeat timeout.** The package keeps the activity alive during the wait by heartbeating
at a third of `HeartbeatTimeout` — but only if you configured one. With no heartbeat timeout there
is no pump, and the wait is bounded solely by the activity timeout. With one set too short for the
review, the activity dies mid-review.

```csharp
opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(20);
opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(1);
opts.DefaultApprovalTimeout  = TimeSpan.FromMinutes(15);  // < ActivityTimeout
```

**Keep `ApprovalTimeout` below `ActivityTimeout`.** Otherwise the activity dies first and leaves
the workflow holding an approval nobody is waiting on — see [Stranded approvals](#stranded-approvals).

**Heartbeating is not durability.** The wait is worker-resident: the activity lives on one worker
for its whole duration, so a deploy or crash ends it. Combined with `NoRetry()` on a write tool,
that ends the turn. This is the reason to prefer workflow-parked approval for anything unattended.

Prefer `NoRetry()` for non-idempotent effects, or make the effect idempotent with an
application-owned key.

---

## Stranded approvals

If the tool activity ends mid-review — timeout, cancellation, worker loss — the workflow **keeps
its pending approval**. Recovery is explicit, not automatic.

Ordinary runs still go through: nothing in the `RunAgentAsync` validator looks at pending
approvals. What fails is the *next approval request*, with
`DurableApprovalAlreadyPending` — only one approval may be outstanding at a time. So the session
keeps working until it next needs a human, then jams.

```csharp
var stranded = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
if (stranded is not null)
{
    await client.CancelPendingApprovalAsync(sessionId, "Reviewer went away.", cancellationToken);
}
```

`CancelPendingApprovalAsync` resolves the request as rejected on the caller's behalf. It is a no-op
when nothing is pending, and it ignores an already-resolved request, so it is safe to call
speculatively during recovery.

---

## Expiring session grants

A reviewed decision can be reused for matching calls later in the *same* session. Mark eligible
tools `ScopeAware()` and register the interceptor with `UseApprovalScopes()`:

```csharp
agent.AddTool(writeFile, tool => tool.NoRetry().RequireApproval().ScopeAware());
agent.UseApprovalScopes(options =>
{
    options.MaxSessionScopeRecords = 128;
    options.MaxSessionScopeBytes   = 16 * 1024;
});
```

Ordinary `ResolveApprovalAsync` still applies to exactly one call. Reusable grants require a
separately registered administrative service — deliberately not part of normal agent setup:

```csharp
services.AddTemporalAgentApprovalScopeAdministration();

var grant = await scopeAdmin.GrantSessionScopeAsync(
    sessionId,
    new SessionApprovalScopeGrantRequest
    {
        RequestId = pending.RequestId,
        Pattern   = new ApprovalScopePattern
        {
            Type      = PatternMatchType.Glob,
            Parameter = "path",
            Pattern   = "/tmp/*",
        },
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        Actor     = reviewerName,
        Reason    = ticketId,
    },
    cancellationToken);

await scopeAdmin.RevokeSessionScopeAsync(sessionId, grant.GrantId!, cancellationToken);
```

Exactly one constraint is required: either `Pattern` or `MatchAllArguments = true`. Grants expire
on workflow time, survive continue-as-new, are bounded in workflow state, and never cross sessions.
Permanent and cross-session grants are intentionally unsupported.

Keep the admin service behind an authenticated backend. The application must authorize the
underlying resource *before* turning it into a `TemporalAgentSessionId` — the package cannot infer
tenant ownership from a workflow ID. `Actor`, `Reason`, approval descriptions, and review data are
untrusted data, not authentication evidence.

**Approval is not effect-time authorization.** A tool that changes an external system must re-read
current tenant, ownership, and authorization state immediately before performing the effect —
especially after a long approval wait.

---

## Where approval does not work

Two execution paths cannot park for external review, because neither host workflow carries the
approval state machine:

- **Workflow-local sub-agents** — `WorkflowAgents.GetTemporalAgent(...)` inside your own workflow.
- **Scheduled jobs** — runs hosted by `AgentJobWorkflow`.

On both paths, `RequireApproval()` and an interceptor's `PauseForApproval()` **degrade to Block**:
the tool does not run, and the model receives a synthetic blocked result. In-tool
`TemporalAgentContext.Current` throws, with a message naming which of the two paths you are on and
what to use instead.

If work on those paths needs human review, own the approval in your orchestrating workflow, or
drive the agent through a managed session via `TemporalAIAgentProxy`.

---

## Timeouts and failure behaviour

- `ApprovalTimeout` bounds the durable wait and **resolves a timeout as a rejection**, with a
  reason naming the window that elapsed.
- Cancellation propagates as cancellation. It is never converted into an approval denial.
- Only one approval is pending at a time; a second request while one is outstanding fails with
  `DurableApprovalAlreadyPending`. Multiple approvals from one model response are handled in
  deterministic call order.
- The latest 32 resolutions are retained across continue-as-new for retry deduplication.

---

## Samples

| Sample | Shows |
|---|---|
| `samples/MAF/HumanInTheLoop` | Short in-tool approval, with heartbeat and timeout budget set |
| `samples/MAF/ToolInterceptor` | Workflow-parked policy and reviewer-safe metadata |
| `samples/MAF/ApprovalScopes` | One-call decisions plus constrained, expiring session grants |

See also [Durable approvals](../../concepts/durable-approvals.md) and
[Tool interceptor](tool-interceptor.md).
