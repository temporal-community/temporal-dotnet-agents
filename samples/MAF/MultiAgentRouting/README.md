# Multi-Agent Routing: Activity-Based Dispatch

This sample demonstrates two regular workflow patterns over registered Temporal agents:

- `RoutingWorkflow` classifies the request in an activity, then invokes the selected specialist in
  another activity.
- `ParallelAgentWorkflow` schedules one proxy-invocation activity per specialist and collects the
  responses with `Workflow.WhenAllAsync`.

Each agent invocation receives a workflow-generated session ID and request ID. The scoped activity
resolves the registered proxy by its exact registration name. This keeps workflow logic
deterministic and preserves request identity if an activity is retried.

The worker also demonstrates OpenTelemetry tracing with Temporal's `TracingInterceptor` and the
Temporal Agents activity sources. See [`Program.cs`](./Program.cs) for the complete registration
and tracer configuration.

## Run

Start Temporal with the required search attributes and configure `OPENAI_API_KEY` and
`OPENAI_API_BASE_URL`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/MultiAgentRouting/MultiAgentRouting.csproj
```
