# Skills: Progressive-Disclosure Instructions for Durable Agents

## Overview

This sample gives a durable agent a catalog of instructions without adding every instruction to
every prompt. The agent receives a compact skill index and calls the durable `load_skill` tool
only when it needs the full content.

It registers two skills:

- `expense-report` — loaded from `skill-catalog/expense-report/SKILL.md`
- `meeting-summary` — registered inline in code

The first conversation asks about expense reporting; the second asks about meeting summaries.
Each `load_skill` request runs as a Temporal activity, and the session StateBag carries the
compact index across turns and continue-as-new boundaries.

## Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`)
- An OpenAI-compatible API key and base URL

## Configure credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/Skills
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/Skills
```

The sample uses `gpt-4o-mini`; `TEMPORAL_ADDRESS` defaults to `localhost:7233`.

## Run

```bash
dotnet run --project samples/MAF/Skills/Skills.csproj
```

## What to look for

- The agent calls `load_skill("expense-report")` before answering the first question.
- The next turn loads `meeting-summary` without placing every skill body in the prompt.
- Temporal Web UI shows the durable tool activities used to fetch the skill content.

## Further reading

- [Skills guide](../../../docs/how-to/MAF/skills.md)
- [Tool interceptor sample](../ToolInterceptor/README.md)
- [Context provider sample](../ContextProviders/README.md)
