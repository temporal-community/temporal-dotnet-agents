# Ambient Agent

This sample runs long-lived monitor and alert workflows. Each workflow schedules a scoped activity
to invoke its registered agent proxy, passing a deterministic session ID and request ID generated
by workflow code. The monitor continues to carry its business readings across Continue-as-New;
agent conversation state remains owned by the registered `AgentWorkflow`.

The sample also demonstrates signals, queries, graceful shutdown, and workflow Continue-as-New.

For this demo, CPU or memory at least 90%, or temperature at least 85 degrees Celsius, is anomalous.
The driver sends 20 readings in four five-reading batches; only the third batch contains spikes.
It checks for three normal analyses, one anomaly analysis, one nonempty alert notification, and no
pending readings after shutdown. Unexpected counts or classifications produce a nonzero exit code
and no completion marker. These illustrative thresholds are not a production monitoring policy.

Health readings are queued FIFO for analysis in exact `AnalysisInterval` batches, even while an
analysis activity is running. Each anomaly alert includes the exact batch that was analyzed.
`MaxBufferSize` limits the rolling status history, not the pending-analysis queue; this sample
does not impose a backlog limit. Continue-as-New carries pending readings in their original order.

Shutdown closes ingestion: signals processed after shutdown are ignored. The monitor drains
complete batches and leaves any trailing partial batch unanalyzed, reported as
`PendingAnalysisReadings`. The alert workflow drains alerts already received before it stops.
The driver awaits monitor completion before stopping alerts, rather than relying on a fixed delay.
Terminal agent-turn failures fail the workflow instead of skipping a batch or reporting a
successful drain; transient proxy failures remain retryable.

Workflow patches preserve the earlier batching and shutdown commands for existing executions.
New executions, including new runs after Continue-as-New, use the FIFO and drain behavior above.

## Run

Start Temporal with the required search attributes and configure `OPENAI_API_KEY` and
`OPENAI_API_BASE_URL`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/AmbientAgent/AmbientAgent.csproj
```
