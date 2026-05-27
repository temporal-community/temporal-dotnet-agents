# HumanInTheLoop: Approval Gates for Durable Chat

## Overview

This sample demonstrates how to suspend a durable chat session mid-turn and require explicit human
approval before a destructive tool call proceeds. The workflow blocks on `WaitConditionAsync` — no
polling on the workflow side — and resumes only when an external caller submits a decision via
`SubmitApprovalAsync`. The sample auto-approves to show the full flow end-to-end.

- A `delete_records` tool calls the `RequestApproval` workflow update and suspends until approved
- External code polls `GetPendingApprovalAsync` then calls `SubmitApprovalAsync` to unblock
- The workflow holds the full conversation in history — no state is lost during the approval wait
- `ActivityTimeout` and `ApprovalTimeout` must both cover the full human review window (set to 24 hours here)
- `GetHistoryAsync` after the turn confirms tool call and result messages are persisted

## Flow

```
sessionClient.ChatAsync(...)           ← starts; blocks inside tool
    │
    └─ delete_records tool
           │
           ├─ handle.ExecuteUpdateAsync("RequestApproval", ...)
           │       workflow: stores request, WaitConditionAsync
           │
           └─ [suspended — background task heartbeats every 4 min to keep activity alive]

sessionClient.GetPendingApprovalAsync(conversationId)   ← returns pending request
sessionClient.SubmitApprovalAsync(conversationId, decision)

    workflow: WaitConditionAsync satisfied
    tool: receives DurableApprovalDecision, performs delete
    LLM: receives tool result, generates final response

ChatAsync returns with assistant's final message
```

## Highlights

- **No spin-wait in the workflow.** The workflow blocks on `WaitConditionAsync` until `_approvalDecision` is set. No timers, no polling loops, no wasted server resources.
- **Heartbeating is manual — the SDK does not do it automatically.** Once LLM token streaming ends, the activity goes silent. A background `Task` inside the tool closure calls `ActivityExecutionContext.Current.Heartbeat(...)` every 4 minutes (well under the 10-minute `HeartbeatTimeout`) for the duration of the `ExecuteUpdateAsync` wait. Without this, the server would declare the activity failed after `HeartbeatTimeout` and schedule a retry, re-issuing the approval request.
- **`ActivityTimeout` must cover human review time.** If the activity times out before approval arrives, Temporal will retry it — re-triggering the tool and issuing a duplicate approval request. Set both `ActivityTimeout` and `ApprovalTimeout` to a value comfortably longer than your expected review window.
- **`RequestApproval` is a `[WorkflowUpdate]`, not a signal.** Updates are synchronous from the caller's perspective — `ExecuteUpdateAsync` blocks until the update handler returns, which is after `SubmitApprovalAsync` resolves the `WaitConditionAsync`.
- **The conversation is fully preserved during suspension.** Because the workflow holds history in-memory (persisted via Temporal), the LLM receives the complete tool result when it resumes — no message re-assembly required.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/HumanInTheLoop
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/HumanInTheLoop
```

### Run

```bash
dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj
```

### Expected Output

> Tool and main-thread output can interleave slightly depending on scheduling.
> The order below reflects the typical sequence.

```
╔══════════════════════════════════════════════════════════╗
║   Data Management Assistant — HITL Approval Sample       ║
╠══════════════════════════════════════════════════════════╣
║  The assistant can delete records but requires approval.  ║
║  This sample auto-approves to demonstrate the full flow.  ║
╚══════════════════════════════════════════════════════════╝

════════════════════════════════════════════════════════════
 Demo: Human-in-the-Loop Tool Approval
════════════════════════════════════════════════════════════
 Conversation ID: hitl-demo-<guid>

 User : Delete all records older than 30 days.

 [Main] Chat started — polling for pending approval...


 [Tool] delete_records called (olderThan=30 days)
 [Tool] Sending approval request to workflow...

 ╔══════════════════════════════════════════════════╗
 ║           APPROVAL REQUIRED                      ║
 ╠══════════════════════════════════════════════════╣
 ║  Request ID  : a3f1b2c4...                       ║
 ║  Function    : delete_records                    ║
 ║  Description : Permanently delete all records old║
 ╚══════════════════════════════════════════════════╝

 [Reviewer] Auto-approving request to demonstrate the full flow...
 [Reviewer] Approval submitted — waiting for assistant response...

 [Tool] Approval decision received: APPROVED
 [Tool] Reason: Auto-approved by sample reviewer.
 [Tool] Deleting records older than 30 days...
 Assistant: I have successfully deleted all records older than 30 days.

 [History] 6 messages persisted in workflow state.
════════════════════════════════════════════════════════════

Done.
```
