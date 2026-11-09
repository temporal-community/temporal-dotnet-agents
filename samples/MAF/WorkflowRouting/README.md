# Workflow Routing

This sample demonstrates static and dynamic routing in regular Temporal workflows:

- `CustomerServiceWorkflow` calls a registered classifier through an activity, selects a
  specialist from its result, then calls that specialist through another activity.
- `DynamicRoutingWorkflow` first queries registered agent descriptors in an activity, uses a
  classifier proxy to select an agent, validates the selected registration in an activity, and
  invokes the specialist through an activity.

Each agent call uses a workflow-generated session ID and request ID. The activity resolves the
registered proxy; workflow code never invokes an agent directly.

## Run

Start Temporal with the required search attributes and configure `OPENAI_API_KEY` and
`OPENAI_API_BASE_URL`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/WorkflowRouting/WorkflowRouting.csproj
```
