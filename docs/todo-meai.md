# Temporalio.Extensions.AI — Open Design Todos

Tracked design decisions and documentation work not yet completed.

---

## 1. Add `IDurableChatSessionClient` interface

**Status:** ✅ Implemented
**Priority:** Medium

### Decision

Add a `IDurableChatSessionClient` interface implemented by `DurableChatSessionClient`.
The motivation is NOT to make the class itself unit-testable — it is thin infrastructure
(a Temporal protocol adapter) and its correct behaviour is proven by integration tests
using `WorkflowEnvironment.StartLocalAsync()`.

The motivation IS to make **application code that depends on it** testable. Any service,
controller, or background job that takes `DurableChatSessionClient` as a dependency
cannot currently be tested without a live Temporal connection. An interface solves this
at zero cost to the implementation.

### Precedent

`ITemporalAgentClient` in `Temporalio.Extensions.Agents` exists for exactly this reason.

### Interface surface

```csharp
public interface IDurableChatSessionClient
{
    Task<ChatResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<DurableApprovalDecision> SubmitApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);
}
```

### Implementation changes

- `DurableChatSessionClient` implements `IDurableChatSessionClient`
- `AddDurableAI` registers both: the concrete `DurableChatSessionClient` singleton
  AND an alias so `IDurableChatSessionClient` resolves from DI
- `DurableChatSessionClient` stays `sealed` — the interface is the extension point,
  not the class

### What NOT to do

Do not write unit tests for `DurableChatSessionClient` that mock `ITemporalClient`
and assert that `StartWorkflowAsync` / `ExecuteUpdateAsync` were called. Those tests
only verify the Temporal SDK's API surface, not any logic in this library.

---

## 2. MEAI how-to documentation

**Status:** Stubs created, content not yet written
**Priority:** Low (samples cover the basics for now)

Stub files exist at the paths below. Fill them in when the library is more stable:

| File | Content |
|---|---|
| `docs/how-to/MEAI/usage.md` | Getting started, registration, `AddChatClient` pattern, `DurableAIDataConverter` requirement |
| `docs/how-to/MEAI/testing.md` | When to use unit tests vs integration tests; `IDurableChatSessionClient` stub pattern; `WorkflowEnvironment` fixture |
| `docs/how-to/MEAI/observability.md` | The 4 `ActivitySource` names, span hierarchy diagram, OTel registration snippet |
| `docs/how-to/MEAI/hitl-patterns.md` | HITL flow end-to-end: tool triggers `RequestApprovalAsync`, external caller polls and submits |
| `docs/architecture/MEAI/durable-chat-pipeline.md` | How `DurableChatClient`, `DurableChatWorkflow`, `DurableChatActivities`, and `DurableChatSessionClient` relate; `Workflow.InWorkflow` dispatch guard; ContinueAsNew with history carryover |
