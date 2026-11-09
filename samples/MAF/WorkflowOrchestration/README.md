# Workflow Orchestration through Activities

This sample shows a regular Temporal workflow invoking a registered MAF agent through a scoped
activity. The workflow generates a deterministic `TemporalAgentSessionId` and `RequestId`; the
activity resolves the registered proxy and passes both values through to the session workflow.
This keeps workflow code deterministic and makes retries reuse the same logical agent turn.

The default run waits for the response. The activity also accepts a fire-and-forget option for
application use, but the program does not exercise that branch. The model/tool loop remains owned
by the registered agent's `AgentWorkflow`.

## Run

Start Temporal with the required search attributes:

```bash
temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int
```

Configure `OPENAI_API_KEY` and `OPENAI_API_BASE_URL`, then run:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/WorkflowOrchestration/WorkflowOrchestration.csproj
```
