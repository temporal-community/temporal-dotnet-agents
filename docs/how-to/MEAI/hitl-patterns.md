# Human-in-the-Loop Patterns

Approval routing and application authorization are separate. Follow the normative
[security boundary](../../security.md) for reviewer endpoints and effectful tools.

Managed durable sessions can require approval for individual registered tools. Configure the tool
at worker registration; the workflow owns the wait and does not dispatch the tool activity until a
reviewer approves it.

```csharp
builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.ApprovalTimeout = TimeSpan.FromHours(24);
        options.SessionTimeToLive = TimeSpan.FromHours(26);
    })
    .AddDurableTool(deleteRecords, tool => tool.NoRetry().RequireApproval());
```

`RequireApproval()` is an absolute configuration-time floor: an interceptor cannot turn it into a
non-approved call. `NoRetry()` is normally appropriate for a destructive tool unless its operation
is explicitly idempotent.

## Flow

```
SendAsync
  → GetChatStep returns a FunctionCallContent
  → workflow records DurableApprovalRequest and waits
  → reviewer reads GetPendingApprovalAsync
  → reviewer calls ResolveApprovalAsync
  → workflow either schedules InvokeFunction or returns a denial result to the model
  → GetChatStep continues until a final assistant response
```

The waiting occurs in workflow state. No tool activity is held open and no tool-side heartbeat is
needed while a human reviews the request. `SessionTimeToLive` must be longer than
`ApprovalTimeout` so the session does not exit first.

## Read and resolve an approval

```csharp
var pending = await sessionClient.GetPendingApprovalAsync(conversationId);
if (pending is not null)
{
    var resolution = await sessionClient.ResolveApprovalAsync(conversationId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved = true,
        Reason = "Approved by the review service.",
    });

    if (resolution.Status is not DurableApprovalResolutionStatus.Accepted and
        not DurableApprovalResolutionStatus.AlreadyResolved)
    {
        throw new InvalidOperationException($"Approval was not accepted: {resolution.Status}");
    }
}
```

`GetPendingApprovalAsync` returns `null` when no request is pending. A timed-out approval is
treated as denied and the workflow supplies a denial result to the model rather than running the
tool. `ResolveApprovalAsync` is safe to retry after an ambiguous update-RPC result: an identical
retry returns `AlreadyResolved`; a conflicting decision returns `Conflict`.

Every pending request includes `ExpiresAt`, the workflow-time deadline used for automatic denial.
Set a tool-specific deadline when approval urgency differs by operation:

```csharp
worker.AddDurableTool(deleteRecords, tool =>
    tool.NoRetry().RequireApproval().WithApprovalTimeout(TimeSpan.FromHours(2)));
```

That value is captured when the session starts and survives Continue-As-New. Tools without an
override use `DurableExecutionOptions.ApprovalTimeout`.

## Reviewer-safe context

`DurableApprovalRequest.ReviewData` contains only explicit metadata supplied by a durable tool
interceptor. The workflow never copies raw model-produced function arguments into the approval
request. This avoids exposing secrets or unreviewed payloads to an approval UI.

```csharp
return DurableToolDecision.PauseForApproval(
    "Delete the requested tenant records.",
    metadata: new Dictionary<string, string>
    {
        ["tenant"] = "contoso",
        ["policy"] = "retention-delete",
    });
```

The reviewer receives these values in `pending.ReviewData`; include only information that is safe
and useful for the reviewer. `ReviewData` is not a reviewer identity, credential, or authorization
grant. Authenticate the reviewer in the external approval UI and authorize the tool against current
authoritative state immediately before it performs an effect.

## Interceptors

`IDurableToolInterceptor<DurableToolContext>` can classify, block, skip, modify, or request
approval for a tool call. Use it for policy that depends on the call arguments or external state.
Use `RequireApproval()` for a tool that must always be reviewed. See the
[tool interceptor sample](../../../samples/MEAI/ToolInterceptor/README.md).

Do not implement approval by passing a tool in `ChatOptions.Tools` or by putting
`UseFunctionInvocation()` on the session client. Both bypass the managed-session contract.

## Approval batches

For one model response, the workflow records all interceptor decisions, resolves every required
approval, and only then schedules any approved tool activity. An allowed sibling tool therefore
cannot execute while another tool from that response is awaiting review.
