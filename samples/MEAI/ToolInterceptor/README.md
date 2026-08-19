# ToolInterceptor: IDurableToolInterceptor in a MEAI Durable Chat Session

## Overview

This sample demonstrates `IDurableToolInterceptor<DurableToolContext>` — the pre-tool lifecycle
hook in `TemporalCommunity.Extensions.AI`. The interceptor fires as a Temporal activity before each
durable tool dispatch, allowing you to apply policy, enrich approval context, or short-circuit
execution before the tool activity runs.

The scenario is a "file assistant" with two tools:

- `read_file(name)` — read a file from an in-memory store (safe, read-only)
- `delete_file(name)` — permanently delete a file (write operation, irreversible)

An `AuditInterceptor` enforces file-deletion policy before any delete tool activity executes.

> **Note:** This sample uses `IDurableToolInterceptor<DurableToolContext>` — the base-library
> interface from `TemporalCommunity.Extensions.AI`. For MAF sessions where you need `AgentName` or
> `StateBag` in your interceptor, implement `IAgentToolInterceptor` instead
> (see `samples/MAF/ToolInterceptor/`).

## What It Demonstrates

### All four decision outcomes

| Decision | When | Effect |
|---|---|---|
| `Proceed` | `read_file` (or any non-delete tool) | Tool activity runs; `metadata` carries an `"audit"` tag |
| `Block` | `delete_file` for a protected file (`system.lock`, `kernel.sys`) | Tool activity is NOT invoked; block reason is fed back to the LLM as a tool result |
| `PauseForApproval` | `delete_file` for any unprotected file | Dispatch loop parks; workflow waits for `ResolveApprovalAsync` |
| `Skip` | (not shown — see `DurableToolDecision.Skip` for the synthetic-result path) | Tool activity is not invoked; a synthetic result is injected instead |

### Per-tool opt-outs and floors

- **`SkipInterceptor()`** on `read_file` — the `RunToolInterceptor` activity is not dispatched
  at all; the tool proceeds directly to `InvokeFunction`. Use for read-only tools where policy
  evaluation adds no value.

- **`RequireApproval()`** on `delete_file` — Rule 2, the absolute configuration-time floor.
  Even if the interceptor returned `Proceed`, the dispatch loop would still pause for human
  approval. In this sample the interceptor also returns `PauseForApproval`, so both gates agree.

### Approval poll and submit

Turn 3 (`delete config.json`) starts `SendAsync` in the background, then polls
`GetPendingApprovalAsync` every 500 ms until the interceptor's enriched description appears.
The program auto-approves the request via `ResolveApprovalAsync`, unblocking the workflow so the
delete activity can proceed.

### Three-turn conversation

| Turn | User message | Interceptor outcome | What happens |
|---|---|---|---|
| 1 | "What's in config.json?" | Skipped (`SkipInterceptor()`) | `read_file` runs directly as a Temporal activity |
| 2 | "Delete system.lock" | `Block` | LLM told the file is protected; `delete_file` never runs |
| 3 | "Delete config.json" | `PauseForApproval` | Workflow parks; program auto-approves; `delete_file` runs |

## Architecture

```
Program.cs
    │
    ├─ AddDurableAI(opts => opts.DefaultToolInterceptor = ...)
    │       └─ AuditInterceptor registered in DurableExecutionOptions
    │
    ├─ AddDurableTool(readFileTool,   opts => opts.SkipInterceptor())
    ├─ AddDurableTool(deleteFileTool, opts => opts.NoRetry().RequireApproval())
    │
    └─ DurableChatSessionClient.SendAsync(...)
           │
           └─ DurableChatWorkflow (managed tool-dispatch loop)
                  │
                  ├─ GetChatStepAsync  ← LLM call activity
                  │
                  ├─ RunToolInterceptor  ← AuditInterceptor.BeforeToolCallAsync
                  │       └─ returns Block / PauseForApproval / Proceed
                  │
                  └─ InvokeFunction   ← tool activity (only if Proceed / after approval)
                         └─ FakeFileSystem.ReadFile / DeleteFile
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)
- An OpenAI API key

### Configure

```bash
# Required — keep out of appsettings.json
dotnet user-secrets set OPENAI_API_KEY sk-... --project samples/MEAI/ToolInterceptor

# Optional — defaults to https://api.openai.com/v1 and gpt-4o-mini
dotnet user-secrets set OPENAI_API_BASE_URL https://api.openai.com/v1 --project samples/MEAI/ToolInterceptor
dotnet user-secrets set OPENAI_MODEL gpt-4o-mini --project samples/MEAI/ToolInterceptor
```

### Run

```bash
dotnet run --project samples/MEAI/ToolInterceptor/ToolInterceptor.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 Turn 1: Read a file (interceptor skipped via SkipInterceptor)
════════════════════════════════════════════════════════
 User : What's in config.json?
 Agent: The contents of config.json are: { "version": "1.4.2", "debug": false, "maxRetries": 3 }
 (Interceptor was skipped — read_file ran directly as an activity)
════════════════════════════════════════════════════════

════════════════════════════════════════════════════════
 Turn 2: Delete a protected file (interceptor → Block)
════════════════════════════════════════════════════════
 User : Delete system.lock
 Agent: I'm unable to delete system.lock — it is a protected system file and cannot be deleted.
 (AuditInterceptor blocked the tool — delete_file was never invoked)
════════════════════════════════════════════════════════

════════════════════════════════════════════════════════
 Turn 3: Delete config.json (PauseForApproval → auto-approve)
════════════════════════════════════════════════════════
 User : Delete config.json
 [Main] Starting chat (will block waiting for approval)...

 ╔══════════════════════════════════════════════════╗
 ║           APPROVAL REQUIRED                      ║
 ╠══════════════════════════════════════════════════╣
 ║  Request ID  : a1b2c3d4...                       ║
 ║  Function    : delete_file                       ║
 ║  Description : Delete file 'config.json' — this  ║
 ╚══════════════════════════════════════════════════╝

 [Reviewer] Auto-approving to demonstrate the full flow...
 [Reviewer] Approval submitted — waiting for assistant response...

 Agent: I have successfully deleted config.json.
 (delete_file ran after human approval — file is now gone from FakeFileSystem)
════════════════════════════════════════════════════════

Done.
```
