# Human in the Loop: Approval Gates for Durable Chat

This sample shows the managed-session approval path for a destructive tool. The `delete_records`
function is registered with `AddDurableTools` and `RequireApproval()`. When the model requests it,
the workflow records an approval request and waits without running the tool activity. An external
caller reads the request with `GetPendingApprovalAsync` and resolves it with `ResolveApprovalAsync`.

```
sessionClient.SendAsync(...)
    │
    ├─ GetChatStep activity returns delete_records
    ├─ workflow exposes a pending DurableApprovalRequest and waits
    ├─ external reviewer calls ResolveApprovalAsync
    ├─ InvokeFunction activity runs delete_records (only if approved)
    └─ GetChatStep activity produces the final assistant response
```

The sample auto-approves so the complete path can run unattended. Replace that block with a UI,
webhook, or other review system in a real application.

## Important details

- `RequireApproval()` is a workflow-owned gate. The tool does not call Temporal APIs and no
  activity remains running while a human reviews the request.
- `SessionTimeToLive` must outlast the approval timeout; this sample uses a 24-hour
  per-tool timeout (`WithApprovalTimeout`) and a 26-hour session lifetime.
- `NoRetry()` is deliberate: deleting records is a write operation and should not be repeated by
  an activity retry without an idempotency design.
- `ChatOptions.Tools` and `UseFunctionInvocation()` are not used. Managed sessions obtain both
  the model-visible schema and the worker implementation from `AddDurableTools`.

## Run

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/HumanInTheLoop
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/HumanInTheLoop
dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj
```

Start a local Temporal server first, for example with `temporal server start-dev`.
