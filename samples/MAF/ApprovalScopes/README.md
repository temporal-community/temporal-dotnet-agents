# Expiring Session Approval Grants

This sample shows two deliberately separate human-in-the-loop capabilities:

- `ITemporalAgentClient.ResolveApprovalAsync` approves or denies exactly one pending tool call.
- The opt-in `ITemporalAgentApprovalScopeAdministration` service approves a pending call and creates a bounded, expiring grant for later matching calls in the same session.

`UseApprovalScopes()` installs the worker-side interceptor that evaluates active session grants. The write tool uses `RequireApproval().ScopeAware()`, while the read-only tool uses `SkipInterceptor()`.

## Run

Prerequisites:

- .NET 10 SDK
- Temporal Service 1.31 or newer
- an OpenAI-compatible endpoint and key

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/ApprovalScopes
dotnet run --project samples/MAF/ApprovalScopes/ApprovalScopes.csproj
```

When `write_file` pauses, the console offers four choices:

1. deny the call;
2. approve this call only;
3. grant a 30-minute session scope for every `write_file` call;
4. grant a 30-minute session scope only when `path` matches `/tmp/*`.

The administrative service is registered explicitly:

```csharp
services.AddTemporalAgentApprovalScopeAdministration();
```

Do not expose that service directly to an untrusted caller. The application must authenticate the reviewer, authorize the application resource that maps to the `TemporalAgentSessionId`, and then call the administrative service from a trusted backend. `Actor` and `Reason` are audit text supplied by that backend; the library cannot authenticate either value.

The grant lives in workflow state, survives Continue-As-New, expires according to workflow time, and can be revoked by its returned `GrantId`. It does not cross sessions. A matching grant skips the human gate, but it is not effect-time authorization: the tool must still re-check current tenant, ownership, and authorization data immediately before performing an external effect.

`RequestId` resolution is retry-safe. An identical retry reports `AlreadyResolved`; a changed retry reports `Conflict`.
