# Mixed Activities: Regular and AI Activities in One Workflow

## Overview

Shows how to mix plain `[Activity]`-decorated methods with a durable AI agent call in a single Temporal workflow. This is the most common production integration shape — data operations live in regular activities, AI reasoning lives in a durable agent turn, and Temporal makes both fully crash-safe.

The demo processes three support documents through a four-step pipeline:

1. `FetchDocumentAsync` — regular activity, pulls document text from an in-memory store
2. `agent.RunAsync` — durable agent turn, AI classifies the document
3. `StoreAnalysisAsync` — regular activity, persists the analysis result
4. `NotifyReviewerAsync` — regular activity, prints a simulated reviewer alert

## Why This Matters

Many production workflows need to:
- Read data from a database or API (regular activity)
- Run AI reasoning on that data (durable agent)
- Write results back or trigger a downstream system (regular activity)

Temporal treats both kinds of steps as durable. If the worker crashes between steps, replay picks up exactly where it left off — completed steps are not re-executed, whether they were an LLM call or a database write.

## Key Code Pointers

- `DocumentActivities.cs` — three `[Activity]`-decorated methods with no AI involvement
- `DocumentPipelineWorkflow.cs` — the mixing point: `Workflow.ExecuteActivityAsync` and `agent.RunAsync` interleaved in the same `[WorkflowRun]` method
- `Program.cs` — `AddSingletonActivities<DocumentActivities>()` before `AddTemporalAgents(...)` on the same worker builder

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`)
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/MixedActivities
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/MixedActivities
```

### Run

```bash
dotnet run --project samples/MAF/MixedActivities/MixedActivities.csproj
```

### Expected Output

```
Worker started. Submitting document analysis workflows...

Submitted 3 workflows. Waiting for results...

─── Analysis Results ─────────────────────────────────────────────────────────

  Document: doc-001
  Category: Bug
  Summary: Customer is experiencing login failures after resetting their mobile app password.

  Document: doc-002
  Category: Feature
  Summary: User requests the addition of a dark mode option to the dashboard.

  Document: doc-003
  Category: Bug
  Summary: Payment processing is intermittently failing for customers located in the EU.

─── Temporal Web UI ─────────────────────────────────────────────────────────
  Open http://localhost:8233 to inspect the workflow event histories.
  Each workflow shows distinct activity rows:
    • FetchDocumentAsync   — plain [Activity], no AI
    • RunDurableAgentStep  — LLM call dispatched by the durable agent
    • StoreAnalysisAsync   — plain [Activity], no AI
    • NotifyReviewerAsync  — plain [Activity], no AI

Done.
```
