# Human-in-the-loop patterns

Temporal-backed agents support two approval shapes. Choose based on how long the review may take.

## Workflow-parked approval

Use `RequireApproval()` or return `DurableToolDecision.PauseForApproval(...)` from an `IAgentToolInterceptor`. The workflow records a `DurableApprovalRequest`, waits durably, and schedules no tool activity until the reviewer decides or the approval timeout expires. This is the recommended shape for long waits and destructive tools.

All calls in one model-produced batch pass interceptor evaluation and approval before any tool activity is scheduled. An allowed sibling therefore cannot execute while another call in that batch is awaiting review.

```csharp
options.AddDurableAgent("Operations", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.AddTool(sendEmail, tool => tool.NoRetry().RequireApproval());
});
```

An external service uses the typed agent client:

```csharp
var pending = await client.GetPendingApprovalAsync(sessionId, cancellationToken);
if (pending is not null)
{
    var result = await client.ResolveApprovalAsync(
        sessionId,
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
            Reason = "Reviewed under ticket INC-1234.",
        },
        cancellationToken);
}
```

Resolution is retry-safe: an equivalent retry returns `AlreadyResolved`, while a changed retry returns `Conflict`. Reviewer-facing `ReviewData` comes only from interceptor-authored metadata; raw model arguments are not copied automatically.

## In-tool approval

A tool may call `TemporalAgentContext.Current.RequestApprovalAsync`. The workflow waits durably, but the tool activity remains open and heartbeats while waiting. Use this only for short, bounded interactions where the tool must decide exactly where the prompt occurs.

```csharp
static async Task<string> PublishDraftAsync(string draft)
{
    var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Description = "Publish this draft?",
        });

    return decision.Approved ? "Published" : "Not published";
}
```

The tool activity timeout must exceed the approval timeout. Prefer `NoRetry()` for non-idempotent effects, or make the effect idempotent using an application-owned key.

## Expiring session grants

MAF can reuse a reviewed decision for matching calls later in the same session. Register the interceptor with `UseApprovalScopes()` and mark eligible tools with `ScopeAware()`:

```csharp
agent.AddTool(writeFile, tool => tool.NoRetry().RequireApproval().ScopeAware());
agent.UseApprovalScopes(options =>
{
    options.MaxSessionScopeRecords = 128;
    options.MaxSessionScopeBytes = 16 * 1024;
});
```

Ordinary `ResolveApprovalAsync` still applies to one call only. Reusable grants require the separately registered administrative service:

```csharp
services.AddTemporalAgentApprovalScopeAdministration();

var grant = await scopeAdmin.GrantSessionScopeAsync(
    sessionId,
    new SessionApprovalScopeGrantRequest
    {
        RequestId = pending.RequestId,
        Pattern = new ApprovalScopePattern
        {
            Type = PatternMatchType.Glob,
            Parameter = "path",
            Pattern = "/tmp/*",
        },
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        Actor = reviewerName,
        Reason = ticketId,
    },
    cancellationToken);

await scopeAdmin.RevokeSessionScopeAsync(sessionId, grant.GrantId!, cancellationToken);
```

Exactly one constraint is required: `Pattern`, or `MatchAllArguments = true`. Grants expire according to workflow time, survive Continue-As-New, are bounded in workflow state, and never cross sessions. Permanent or cross-session grants are intentionally unsupported.

The admin service is not registered by normal agent setup. Keep it in an authenticated backend. The application must authorize the application resource before turning it into a `TemporalAgentSessionId`; the package cannot infer tenant ownership from a workflow ID. `Actor`, `Reason`, approval descriptions, and review data are untrusted data, not authentication evidence.

Approval is also not effect-time authorization. A tool that changes an external system must re-read current tenant, ownership, and authorization data immediately before performing the effect, especially after a long approval wait.

## Timeouts and failure behavior

- `ApprovalTimeout` bounds the durable wait and resolves timeout as a rejection.
- Cancellation propagates; it must not be converted into an approval denial.
- Scheduled jobs and sub-agent paths cannot park for external review; `PauseForApproval` is treated as a block there.
- Only one approval is pending at a time. Multiple approvals from one model response are handled in deterministic call order.
- The latest 32 resolutions are retained across Continue-As-New for retry deduplication.

## Samples

- `samples/MAF/HumanInTheLoop` — short in-tool approval.
- `samples/MAF/ToolInterceptor` — workflow-parked policy and reviewer-safe metadata.
- `samples/MAF/ApprovalScopes` — one-call decisions plus constrained, expiring session grants.

See also [Durable approvals](../../concepts/durable-approvals.md) and [Tool interceptor](tool-interceptor.md).
