# Workflow Routing

This sample demonstrates static and dynamic routing in regular Temporal workflows:

- `CustomerServiceWorkflow` calls a registered classifier through an activity, selects a
  specialist from its result, then calls that specialist through another activity.
- `DynamicRoutingWorkflow` first queries registered agent descriptors in an activity, uses a
  separate dynamic-classifier agent to select a specialist, resolves the selected name to its
  registered spelling, and invokes the specialist through an activity.

Each agent call uses a workflow-generated session ID and request ID. The activity resolves the
registered proxy; workflow code never invokes an agent directly.

## Run

Start Temporal Service 1.31.0 or later with the required search attributes:

```bash
temporal server start-dev \
  --search-attribute AgentName=Keyword \
  --search-attribute SessionCreatedAt=Datetime \
  --search-attribute TurnCount=Int
```

Configure `OPENAI_API_KEY` and `OPENAI_API_BASE_URL`, then run:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/WorkflowRouting/WorkflowRouting.csproj
```
