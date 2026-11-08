# Ambient Agent

This sample runs long-lived monitor and alert workflows. Each workflow schedules a scoped activity
to invoke its registered agent proxy, passing a deterministic session ID and request ID generated
by workflow code. The monitor continues to carry its business readings across Continue-as-New;
agent conversation state remains owned by the registered `AgentWorkflow`.

The sample also demonstrates signals, queries, graceful shutdown, and workflow Continue-as-New.

## Run

Start Temporal with the required search attributes and configure `OPENAI_API_KEY` and
`OPENAI_API_BASE_URL`:

```bash
dotnet run --project samples/MAF/AmbientAgent/AmbientAgent.csproj
```
