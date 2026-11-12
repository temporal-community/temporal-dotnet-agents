# Pub/Sub and Event-Driven Communication in TemporalAgents

## Why There Is No Pub/Sub

Temporal is not a message broker. It has no built-in pub/sub substrate — no topics, no
fan-out delivery, and no durable queues shared across unrelated workflows.

**Attempting to build a pub/sub layer on top of Temporal is therefore out of scope.**
It would require an external broker (Kafka, NATS, Service Bus, etc.) and would duplicate
Temporal's own durability guarantees in an awkward way.

---

## The Temporal-Idiomatic Equivalent

In Dapr Agents the pub/sub pattern is used to decouple agents that react to events
independently. The Temporal equivalent is **parallel orchestration**: an orchestrating
workflow fans out to multiple agents simultaneously using `Workflow.WhenAllAsync`, collects
their results, and optionally aggregates them.

This gives you:

| Pub/Sub capability | Temporal equivalent |
|---|---|
| Fire-and-forget to N subscribers | N activities invoking registered proxies with `IsFireAndForget = true` |
| Wait for all subscribers to respond | Parallel proxy-invocation activities + `Workflow.WhenAllAsync` |
| Wait for the first subscriber to respond | `Workflow.WhenAnyAsync(tasks)` |
| Fan-out with aggregation | Parallel activities + result aggregation in workflow code |
| Retried delivery to subscribers | Bounded activity retry policy with stable session and request IDs |

---

## Code Example — Fan-Out / Fan-In with Activities

The workflow schedules one activity per agent. Each activity resolves the registered proxy; stable
workflow-generated session and request IDs make an activity retry target the same logical turn.

```csharp
using TemporalCommunity.Extensions.AI.Exceptions;

[Workflow("EventDrivenFanOut")]
public class EventDrivenFanOutWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic, string payload)
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
        var summarySession = WorkflowAgents.NewAgentSessionId("SummarizerAgent");
        var taggingSession = WorkflowAgents.NewAgentSessionId("TaggerAgent");
        var moderationSession = WorkflowAgents.NewAgentSessionId("ModeratorAgent");

        var summary = Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.RunAgentAsync(
                "SummarizerAgent", summarySession.WorkflowId,
                Workflow.NewGuid().ToString("N"), payload),
            agentCallOptions);
        var tags = Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.RunAgentAsync(
                "TaggerAgent", taggingSession.WorkflowId,
                Workflow.NewGuid().ToString("N"), payload),
            agentCallOptions);
        var moderation = Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.RunAgentAsync(
                "ModeratorAgent", moderationSession.WorkflowId,
                Workflow.NewGuid().ToString("N"), payload),
            agentCallOptions);

        var results = await Workflow.WhenAllAsync([summary, tags, moderation]);

        return $"""
            Summary:    {results[0]}
            Tags:       {results[1]}
            Moderation: {results[2]}
            """;
    }
}
```

All three activities exclude retained terminal outcomes: retrying the same request ID returns
the same failure instead of starting another turn. Transport failures, session-busy responses,
and lost completions remain retryable within the budget. See [the bounded invocation retry contract](../../how-to/MAF/usage.md#invoking-agents-from-a-workflow).

### Registration

```csharp
builder.Services.AddChatClient(chatClient);

builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddWorkflow<EventDrivenFanOutWorkflow>()
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SummarizerAgent", agent =>
        {
            agent.Instructions = "Summarize the post.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("TaggerAgent", agent =>
        {
            agent.Instructions = "Generate tags for the post.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("ModeratorAgent", agent =>
        {
            agent.Instructions = "Moderate the post for policy violations.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

---

## Code Example — Fire-and-Forget Fan-Out (No Response Needed)

When the publisher does not need to wait for each subscriber, use
`ITemporalAgentClient.RunAgentFireAndForgetAsync` in a loop. This is the correct
**external** path (e.g., from an API endpoint or console app):

```csharp
// External caller — outside any workflow
var subscriberNames = new[] { "AuditAgent", "NotificationAgent", "AnalyticsAgent" };

foreach (var name in subscriberNames)
{
    var sessionId = TemporalAgentSessionId.WithRandomKey(name);
    // Equivalent to publishing to a topic: fire-and-forget, no reply needed.
    // Update-With-Start under the hood — returns once the update is accepted;
    // the agent session workflow processes the event asynchronously.
    await agentClient.RunAgentFireAndForgetAsync(sessionId, new RunRequest(eventPayload));
}
```

> **Workflow-internal path:** Schedule an application-owned activity that resolves the registered
> `TemporalAIAgentProxy`. For fire-and-forget, pass a workflow-generated `TemporalAgentSessionId`
> and `RequestId`, and set `IsFireAndForget = true` in `TemporalAgentRunOptions`. The activity
> implementation in `samples/MAF/WorkflowOrchestration` demonstrates this pattern.

---

## Key Differences vs. a Message Broker

| Aspect | Pub/Sub broker (e.g. Kafka) | Temporal activity orchestration |
|--------|---|---|
| Subscriber discovery | Dynamic (any consumer group) | Static (known agent names at design time) |
| Ordering guarantees | Per-partition ordering | No cross-agent ordering (each runs independently) |
| Back-pressure | Consumer lag / offset management | Temporal task queue depth |
| Replay | Offset reset | Temporal workflow history |
| Dead-letter handling | DLQ topic | Temporal activity retry policy |

**When to use a message broker instead:** if your subscribers are dynamically discovered
at runtime, if fan-out exceeds dozens of agents, or if cross-service decoupling is a
hard architectural requirement. In those cases, integrate a broker (e.g. Azure Service Bus
or NATS) externally and use `TemporalAgentContext.StartWorkflowAsync` from within an
activity to trigger agent workflows in response to broker events.

---

_Last updated: 2026-02-27_
