# Durability and Determinism in Temporal Agent Workflows

Temporal workflows must reproduce the same command sequence during replay. Workflow code may
therefore make decisions and schedule work, but model calls and other effects run in activities.
For registered MAF agents, the `AgentWorkflow` owns the session history and schedules model-step
and tool activities.

## Calling a Registered Agent from a Workflow

A regular workflow invokes a registered agent through an activity. The activity resolves the
agent's `TemporalAIAgentProxy` and calls it outside workflow context. Generate the session ID and
request ID in workflow code and pass both to the activity:

```csharp
[Workflow]
public sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var sessionId = WorkflowAgents.NewAgentSessionId("ResearcherAgent");
        var requestId = Workflow.NewGuid().ToString("N");

        return await Workflow.ExecuteActivityAsync(
            (AgentInvocationActivities activities) => activities.RunAsync(
                "ResearcherAgent",
                sessionId.WorkflowId,
                requestId,
                $"Research: {topic}"),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(35),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
            });
    }
}
```

The activity parses the session ID, resolves the named proxy from its scoped service provider, and
passes `TemporalAgentRunOptions.RequestId` to `RunAsync`. On replay, the workflow supplies the same
IDs. If the activity retries after the proxy accepted the request, the stable request ID lets the
session workflow join or replay that same turn rather than submit a new one. An activity failure
before acceptance can still cause the agent request to be attempted again; downstream side effects
must use their own idempotency keys.

Do not invoke `AIAgent.RunAsync` or resolve `TemporalAIAgentProxy` from workflow code. Direct
workflow-context proxy invocation is rejected; the activity boundary is required.

## How a Session Turn Is Executed

```text
Regular workflow
  └─ Activity: invoke registered TemporalAIAgentProxy
       └─ ITemporalAgentClient update with stable RequestId
            └─ AgentWorkflow
                 ├─ RunDurableAgentStep activity (one model step)
                 ├─ InvokeAgentTool activity (serially for each model tool call)
                 └─ CompleteAgentTurn activity
```

The activity result is recorded in the calling workflow's history. The agent's conversation state
is recorded in the separate `AgentWorkflow` history. Its continue-as-new transition carries the
session transcript and state forward.

## Replay, Retry, and Continue-as-New

- Completed workflow activities replay from history without re-executing their bodies.
- An activity attempt can execute again after a timeout, lost completion, or configured retry.
  Supply stable request and session IDs from workflow state; do not generate them inside a retrying
  activity.
- A retry of an effectful activity can repeat an external side effect. Use downstream idempotency
  keys and reconcile against the system of record.
- Agent session history and StateBag are owned by the registered `AgentWorkflow`, which carries
  them across its own continue-as-new transitions.
- A regular workflow's activity arguments and its own continued input/state remain the parent
  workflow's responsibility. Do not retain live `AIAgent`, DI, or service objects in workflow
  state.

## Determinism Rules

Inside workflow code:

- Use `Workflow.UtcNow` and `Workflow.NewGuid()`, not system time or random GUID generation.
- Use `Workflow.ExecuteActivityAsync` for external work, including registered agent proxy calls.
- Use `Workflow.WhenAllAsync` when awaiting parallel workflow activity tasks.
- Do not use `Task.Run`, `Task.Delay`, `ConfigureAwait(false)`, or wall-clock APIs.

See [MAF usage](../../how-to/MAF/usage.md#invoking-agents-from-a-workflow) for a complete activity
pattern and [observability](../../how-to/MAF/observability.md) for tracing.
