# Agent Routing Patterns

Route agent requests in a regular Temporal workflow, but invoke each registered agent through an
activity. The workflow owns the routing decision; the activity resolves the registered
`TemporalAIAgentProxy`. Workflow-generated session and request IDs make retries target the same
logical agent turn.

The [`WorkflowRouting` sample](../../../samples/MAF/WorkflowRouting/) demonstrates static and
descriptor-based dynamic routing.

## Static routing

Use an initial agent call to classify the request, then route to a specialist with workflow logic:

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

[Workflow("CustomerServiceWorkflow")]
public class CustomerServiceWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
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
        var classifierSession = WorkflowAgents.NewAgentSessionId("Classifier");
        var classification = (await Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                "Classifier",
                classifierSession.WorkflowId,
                Workflow.NewGuid().ToString("N"),
                userQuestion),
            agentCallOptions))
            .Trim().ToUpperInvariant();

        var specialistName = classification switch
        {
            "ORDERS" => "OrdersAgent",
            "TECH_SUPPORT" => "TechSupportAgent",
            _ => "GeneralAgent",
        };

        var specialistSession = WorkflowAgents.NewAgentSessionId(specialistName);
        return await Workflow.ExecuteActivityAsync(
            (RoutingActivities activities) => activities.RunAgentAsync(
                specialistName,
                specialistSession.WorkflowId,
                Workflow.NewGuid().ToString("N"),
                userQuestion),
            agentCallOptions);
    }
}
```

Activity results are recorded in workflow history. A replay uses the recorded classification and
does not call the classifier again. Each specialist invocation is also an activity, so its result
and retry behavior are independently visible.

## Dynamic routing through a registry activity

When agent registrations vary by deployment, query the registry in an activity rather than from
workflow code. The activity result is recorded, keeping the workflow deterministic on replay:

```csharp
public class RoutingActivities(TemporalAgentsOptions options, IServiceProvider services)
{
    [Activity("WorkflowRouting.GetAvailableAgents")]
    public AgentInfo[] GetAvailableAgents() =>
        options.GetAgentDescriptors()
            .Select(descriptor => new AgentInfo(descriptor.Name, descriptor.Description))
            .ToArray();

    [Activity("WorkflowRouting.ValidateAgent")]
    public string ValidateAgent(string agentName, string fallback) =>
        options.IsAgentRegistered(agentName) ? agentName : fallback;

    [Activity("WorkflowRouting.RunAgent")]
    public async Task<string> RunAgentAsync(
        string agentName,
        string sessionWorkflowId,
        string requestId,
        string message)
    {
        var sessionId = TemporalAgentSessionId.Parse(sessionWorkflowId);
        var agent = services.GetTemporalAgentProxy(agentName);
        var response = await agent.RunAsync(
            message,
            new TemporalAgentSession(sessionId),
            new TemporalAgentRunOptions { RequestId = requestId });
        return response.Text ?? string.Empty;
    }
}

public record AgentInfo(string Name, string Description);
```

The workflow calls `GetAvailableAgents` and `ValidateAgent` through activities, builds a prompt for
the classifier from the recorded descriptors, then dispatches the selected specialist through
`RunAgentAsync`. Always validate a model-selected name before using it as a keyed proxy name and
provide a registered fallback.

## Retry identity and parallel calls

Create session IDs and request IDs in workflow code. Do not generate them inside the activity:
Temporal may retry the activity, and a new identity on each attempt would submit a different
logical turn.

For independent agents, schedule one activity per agent and await them with
`Workflow.WhenAllAsync`. Use a separate session for each independent conversation. Every activity
awaiting a proxy call must exclude retained terminal outcomes as above, including the classifier
and specialist calls in dynamic routing. A retry with the same request ID returns the same
retained failure; transport failures, session-busy responses, and lost completions remain
retryable within the per-invocation budget. Registry lookup and name-validation activities do not
await agent turns and do not need these exclusions. See [the bounded invocation retry contract](./usage.md#invoking-agents-from-a-workflow).

## Rules

- Do not resolve the agent registry or a proxy inside workflow code.
- Do not call a proxy directly from workflow code; use an activity.
- Resolve the proxy by its exact registered name. Session workflow IDs are canonicalized and are
  not a substitute for the registration key.
- Use `Workflow.UtcNow`, `Workflow.NewGuid()`, and workflow-safe task combinators in workflow code.
- Keep activity retry policies aligned with the effects of the agent's tools.

See [Durability and Determinism](../../architecture/MAF/durability-and-determinism.md),
[Agent Orchestration](../../architecture/MAF/agent-to-agent-communication.md), and the
[workflow orchestration sample](../../../samples/MAF/WorkflowOrchestration/README.md).

_Last updated: 2026-09-11_
