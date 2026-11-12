# Agent Orchestration

Registered agents can be composed by a regular Temporal workflow through activities. The workflow
owns routing and sequencing; an activity resolves the registered proxy and invokes its session
workflow. This keeps workflow code deterministic and stores agent transcript state in the
registered agent's `AgentWorkflow`.

## Sequential Calls

A workflow can call one agent, inspect its durable result, and then call another:

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

var agentCallOptions = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromMinutes(35),
    ScheduleToCloseTimeout = TimeSpan.FromHours(2),
    RetryPolicy = new()
    {
        MaximumAttempts = 3,
        NonRetryableErrorTypes =
        [
            nameof(DurableTurnFailedException),
            nameof(DurableTurnOutcomeTooLargeException),
        ],
    },
};
var researcherSession = WorkflowAgents.NewAgentSessionId("Researcher");
var outline = await Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "Researcher",
        researcherSession.WorkflowId,
        Workflow.NewGuid().ToString("N"),
        $"Research: {topic}"),
    agentCallOptions);

var writerSession = WorkflowAgents.NewAgentSessionId("Writer");
var draft = await Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "Writer",
        writerSession.WorkflowId,
        Workflow.NewGuid().ToString("N"),
        $"Write using this outline:\n{outline}"),
    agentCallOptions);
```

The activity receives the exact registered name, the workflow-generated session workflow ID, a
workflow-generated request ID, and the message. It resolves that name with
`GetTemporalAgentProxy`, creates a `TemporalAgentSession`, and passes the request ID through
`TemporalAgentRunOptions`. The activity should not generate retry identities itself.

## Parallel Fan-Out

For independent calls, schedule separate activities and await them with
`Workflow.WhenAllAsync`. Each task has a distinct session and request ID:

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

var agentCallOptions = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromMinutes(35),
    ScheduleToCloseTimeout = TimeSpan.FromHours(2),
    RetryPolicy = new()
    {
        MaximumAttempts = 3,
        NonRetryableErrorTypes =
        [
            nameof(DurableTurnFailedException),
            nameof(DurableTurnOutcomeTooLargeException),
        ],
    },
};
var researchSession = WorkflowAgents.NewAgentSessionId("Researcher");
var summarySession = WorkflowAgents.NewAgentSessionId("Summarizer");

var research = Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "Researcher", researchSession.WorkflowId, Workflow.NewGuid().ToString("N"), topic),
    agentCallOptions);
var summary = Workflow.ExecuteActivityAsync(
    (AgentInvocationActivities activities) => activities.RunAsync(
        "Summarizer", summarySession.WorkflowId, Workflow.NewGuid().ToString("N"), topic),
    agentCallOptions);

var results = await Workflow.WhenAllAsync([research, summary]);
```

Activity and agent-workflow failures are observed by the parent workflow. Each example shares
options only among its proxy-invocation activities. Exclude the two retained terminal-outcome
exception types from every such activity's retry policy: retrying the same request ID retrieves
the same failure, not a new turn. Transport failures, session-busy responses, and lost completions
remain retryable within the budget. Reusing the same session and request IDs on activity retries
prevents accidental submission of a new logical turn. See [the bounded invocation retry contract](../../how-to/MAF/usage.md#invoking-agents-from-a-workflow).

## Communication from Agent Tools

Code executing in a durable agent tool activity can use `TemporalAgentContext.Current` to access
the current session and configured Temporal client operations. For example, a tool can signal or
start a separate business workflow. Keep authorization and effect idempotency in the application
tool implementation.

## Child-Agent Workflow Delegation

The library does not currently expose a child-agent delegation API. Do not invoke agent proxies
directly from workflow code or model a child workflow using workflow-local agent objects. The
activity-based orchestration described above is the supported composition path.

See the [workflow orchestration sample](../../../samples/MAF/WorkflowOrchestration/README.md) and
[durability and determinism](./durability-and-determinism.md).
