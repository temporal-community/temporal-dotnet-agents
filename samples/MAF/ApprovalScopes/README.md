# Approval Scopes: Durable, Scope-Aware Human-in-the-Loop

## Overview

Feature B extends the HITL approval gate with scope-level carry-forward: a single human decision can approve a tool not just for the current call, but for the remainder of a session or for all future sessions. This sample demonstrates all three scope levels — `ThisCallOnly`, `Session`, and `Always` — through an interactive console where you choose the scope level at each approval gate.

`UseApprovalScopes()` installs the built-in `ScopedApprovalInterceptor`, which consults session and always-scope caches before presenting the approval gate. When a matching scope record is found, the interceptor auto-approves the call and the gate is skipped entirely. `Always`-scope decisions are persisted to a `JsonFileApprovalScopeStore` under `~/.temporalagents/approval-scopes/`, so they survive process restarts.

## What This Sample Demonstrates

- `UseApprovalScopes()` registration with a custom `IApprovalScopeStore`
- All three scope levels (`ThisCallOnly`, `Session`, `Always`) at the interactive console
- `ApprovalScopePattern` (Glob matching on the `path` argument) to scope approval by argument value
- Session-scope carry-forward via StateBag — survives `continue-as-new` without re-approval
- Always-scope persistence to `~/.temporalagents/approval-scopes/FileAssistant/` — survives process restarts
- `SkipInterceptor()` on a read-only tool (`list_files`) to bypass the interceptor activity entirely

## Architecture

```
write_file tool called
        │
        ▼
ScopedApprovalInterceptor.BeforeToolCallAsync()
        │
        ├─ Session-scope cache hit?  → Proceed (no approval gate)
        ├─ Always-scope cache hit?   → Proceed (no approval gate)
        └─ No match                 → PauseForApproval
                │
                ▼
        AgentWorkflow.WaitConditionAsync()
                │
        Human reviews via console (scope choice [0-5])
                │
        client.ResolveApprovalAsync(sessionId, new DurableAgentApprovalDecision
                { RequestId, Approved, Scope, ScopePattern })
                │
                ▼
        Scope stored:
          Session → StateBag["temporal.approval_scopes.session"] (carries through continue-as-new)
          Always  → IApprovalScopeStore.AppendAsync()           (persisted to JSON file)
                │
                ▼
        write_file tool resumes → result returned to agent
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev`)
- An OpenAI-compatible API key
- This sample reads scope choices from the console — do not run it with piped stdin

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/ApprovalScopes
```

### Run

```bash
dotnet run --project samples/MAF/ApprovalScopes/ApprovalScopes.csproj
```

## Suggested Session

Try the following sequence to exercise all three scope levels:

**Turn 0 (baseline) — ThisCallOnly:**

```
You: Write 'test' to /tmp/baseline.txt
```

At the approval gate, choose **[1] Approve (this call only)**. The write proceeds. Then ask the
same thing again:

```
You: Write 'test2' to /tmp/baseline.txt
```

The approval gate appears again. `ThisCallOnly` means no scope was saved — every invocation is
individually gated. This establishes the baseline before any carry-forward scope is granted.

**Turn 1 — Session + Glob scope:**

```
You: Write 'Hello Temporal!' to /tmp/hello.txt
```

At the approval gate, choose **[3] Session — paths matching `/tmp/*`**. This approves any `write_file` call
whose `path` argument matches the glob `/tmp/*` for the remainder of the session.

**Turn 2 — Auto-approved by session scope:**

```
You: Write 'Second note' to /tmp/second.txt
```

The gate is skipped — `/tmp/second.txt` matches the active session-scope glob.

**Turn 3 — Outside scope, new approval needed:**

```
You: Write a summary to /docs/report.txt
```

`/docs/report.txt` does not match `/tmp/*`, so the approval gate fires again. Choose
**[4] Always — any write_file** to persist an unrestricted always-scope.

**Turn 4 — Exit and restart:**

Stop the process with `quit`, then re-run:

```bash
dotnet run --project samples/MAF/ApprovalScopes/ApprovalScopes.csproj
```

```
You: Write 'Post-restart note' to /docs/notes.txt
```

The always-scope loaded from `~/.temporalagents/approval-scopes/FileAssistant/` auto-approves
the call — no gate presented. Session-scope records live in the StateBag of the old workflow and are not carried forward; only always-scope records persisted to the store survive a process restart.

## One-Turn Lag Note

A scope granted in the approval decision for turn N is written to StateBag (session scope) or
the store (always scope) at the end of the turn-N approval. It becomes visible to the
`ScopedApprovalInterceptor` from turn N+1 onward. Within the same tool invocation that
produced the scope record, the tool has already been approved and is running — the scope is
redundant for that invocation but effective for all subsequent ones.

## Always-Scope Store Location

```
~/.temporalagents/approval-scopes/FileAssistant/temporal_approval_scopes_always.json
```

Each record contains `ToolName`, `GrantedAt`, `OriginatingRequestId`, and optionally
`Pattern`. To reset persisted always-scopes, delete this file and restart the sample.

## Key Implementation Notes

- **`RequireApproval().ScopeAware()`** on `write_file` — the combination opts the tool into
  scope-aware auto-approval while still requiring explicit approval when no scope matches.
  `UseApprovalScopes()` must be called for tools that combine `RequireApproval()` and
  `ScopeAware()`; builder startup throws `InvalidOperationException` otherwise. Plain
  `ScopeAware()` without `RequireApproval()` is valid without approval scopes, but then it is only
  informational unless a custom interceptor uses the flag.
- **`SkipInterceptor()`** on `list_files` — since `UseApprovalScopes()` installs
  `ScopedApprovalInterceptor`, `list_files` would reach the interceptor and return `Proceed`
  (not scope-aware, no gate). Using `.SkipInterceptor()` avoids the interceptor activity
  dispatch entirely.
- **`UseApprovalScopes()` and `AddToolInterceptor()` are mutually exclusive.** Calling
  either after the other throws `InvalidOperationException` at builder time.
- **`DurableAIJsonUtilities.DefaultOptions`** is required in `JsonFileApprovalScopeStore` to
  correctly serialize `ApprovalScopePattern` (string-enum `PatternMatchType`) and
  `ApprovalScopeRecord`. The default `JsonSerializerOptions.Default` will not handle these types.
- **`JsonFileApprovalScopeStore` is single-worker only.** It uses a process-local `SemaphoreSlim`
  for concurrency control and is safe for single-worker deployments only. For production, back
  `IApprovalScopeStore` with a distributed store (Redis, a database, etc.) so all workers see the
  same always-scope records.
