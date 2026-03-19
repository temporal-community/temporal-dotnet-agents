# Human-in-the-Loop Patterns (Temporalio.Extensions.AI)

> **TODO** — This guide is a stub. See `docs/todo-meai.md` for the planned content.

Topics to cover:
- End-to-end HITL flow: tool triggers `RequestApprovalAsync` → workflow blocks on `WaitConditionAsync` → external caller submits decision via `SubmitApprovalAsync`
- `DurableApprovalRequest` and `DurableApprovalDecision` types
- `GetPendingApprovalAsync` — polling for a pending request from an external system
- `ApprovalTimeout` in `DurableExecutionOptions` — what happens when no human responds
- Setting a long `ActivityTimeout` for activities that may wait on human review
- Integration with external systems (webhooks, Slack, admin dashboards)
- See also: `samples/MEAI/HumanInTheLoop/` for a runnable example
