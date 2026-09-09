# Human-in-the-Loop: Approval Gates for Agent Actions

## Overview

Demonstrates how to pause an agent mid-turn and wait for a human decision before proceeding. The `send_email` tool suspends itself inside a running activity by issuing a `[WorkflowUpdate]`, and the workflow blocks on `WaitConditionAsync` until an external caller submits an approval or rejection.

This sample demonstrates:
- `TemporalAgentContext.Current.RequestApprovalAsync()` suspending a tool inside an activity
- `ITemporalAgentClient.GetPendingApprovalAsync()` polling for pending approvals from outside the workflow
- `ITemporalAgentClient.ResolveApprovalAsync()` resolving the workflow with a retry-safe decision
- Timeout budget sized for an attended review: 20-minute activity, 15-minute worker-level
  approval window, 10-minute per-agent override, 1-minute heartbeat

The console is only a demonstration reviewer. A real endpoint first authenticates the principal,
loads an application-owned resource, authorizes approval of that resource, and only then reads its
server-held `TemporalAgentSessionId`:

```csharp
Authenticate(request.User);
var resource = await operations.FindAsync(routeResourceId);
await authorization.RequireApprovalPermissionAsync(request.User, resource);

var pending = await agentClient.GetPendingApprovalAsync(resource.SessionId);
var result = await agentClient.ResolveApprovalAsync(
    resource.SessionId,
    new DurableApprovalDecision { RequestId = pending!.RequestId, Approved = approved });
```

The `send_email` tool must reauthorize current authoritative state immediately before delivery.
See the repository [security boundary](../../../docs/security.md).

## Architecture

```
User input
    │
    ▼
proxy.RunAsync(messages, session)            ← [WorkflowUpdate] to AgentWorkflow
    │
    ├─ AgentActivities.RunDurableAgentStepAsync()   ← LLM call (returns FunctionCallContent)
    │
    └─ AgentActivities.InvokeAgentToolAsync()       ← activity per tool (20 min timeout)
           │
           └─ send_email tool invoked
                  │
                  └─ RequestApprovalAsync()   ← [WorkflowUpdate]: sends DurableApprovalRequest
                         │                       workflow blocks on WaitConditionAsync
                         │
                  ┌──────┴──────────────────────────────────────┐
                  │  Human review (console in this sample)       │
                  │  client.GetPendingApprovalAsync(sessionId)   │  ← [WorkflowQuery]
                  │  client.ResolveApprovalAsync(sessionId, ...) │  ← [WorkflowUpdate]
                  └──────┬──────────────────────────────────────┘
                         │
                  WaitConditionAsync satisfied → tool resumes
                         │
                  email sent (or rejected) → result returns to workflow,
                                             next RunDurableAgentStepAsync iteration runs
```

## Highlights

- **Suspension without polling.** The workflow blocks on `WaitConditionAsync` — no spin-wait, no timer. The worker thread is released and other workflows continue normally while waiting.
- **`GetPendingApprovalAsync` is a `[WorkflowQuery]`.** Queries never block the workflow and are safe to call as frequently as needed. This sample polls every second from outside the workflow while the agent task is in-flight.
- **`ResolveApprovalAsync` is a retry-safe `[WorkflowUpdate]`.** It reports `Accepted`, `AlreadyResolved`, or a non-success status so a reviewer can safely retry after an ambiguous client response.
- **`ActivityTimeout` must exceed `ApprovalTimeout`.** An in-tool approval is held open by a live activity, so the activity has to outlive the window it is holding. This sample sets `DefaultActivityTimeout = 20 min`, `DefaultApprovalTimeout = 15 min`, and a per-agent `agent.ApprovalTimeout = 10 min`; the heartbeat timeout is 1 minute and the package heartbeats at a third of it for the whole wait. Get this backwards and the activity expires while the workflow still holds an open approval: ordinary turns keep working, but the next approval request fails with `DurableApprovalAlreadyPending` until the stale one is resolved or its own `ApprovalTimeout` expires. **Note the shipped defaults do not satisfy this rule** — `DefaultActivityTimeout` is 5 minutes and `DefaultApprovalTimeout` is 7 days, so in-tool approval always requires setting them explicitly.
- **`send_email` is registered with `opts.NoRetry()`.** The tool delivers an email after the human approves. Without `NoRetry()`, a transient failure immediately after delivery (before the activity reports success) would cause Temporal to retry the activity — re-entering the approval gate, issuing a second approval request, and potentially sending the email a second time. Write-style tools that produce side effects must set `MaximumAttempts = 1`.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`)
- An OpenAI-compatible API key
- This sample waits for you to type `approve` or `reject` at the console — do not run it with piped stdin

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/HumanInTheLoop
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/HumanInTheLoop
```

### Run

```bash
dotnet run --project samples/MAF/HumanInTheLoop/HumanInTheLoop.csproj
```

### Expected Output

```
Email Assistant — HITL Approval Sample
  Ask the assistant to send an email.
  When it tries, you will be prompted to approve or reject before it is delivered.
  Type 'quit' to exit.

You: Send an email to alice@example.com saying the meeting is at 3pm
Assistant: (thinking...)

  ╔══════════════════════════════════════════════╗
  ║            APPROVAL REQUIRED                 ║
  ╠══════════════════════════════════════════════╣
  ║  Send email to alice@example.com             ║
  ║  Subject: Meeting at 3pm                     ║
  ╚══════════════════════════════════════════════╝
  Decision [approve/reject]: approve

  Approved — agent is resuming...

  [EMAIL SENT] To: alice@example.com
               Subject: Meeting at 3pm
Assistant: The email has been sent to alice@example.com.
```
