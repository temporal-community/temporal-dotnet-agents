# Agent Routing Patterns

How TemporalAgents routes messages to the right agent — from framework-managed LLM routing to fully custom workflow-based routing. This document covers all three patterns, when to use each, and critical determinism considerations.

---

## Table of Contents

1. [Overview](#overview)
2. [Three Routing Approaches](#three-routing-approaches)
3. [Pattern 1: IAgentRouter + RouteAsync](#pattern-1-iagentrouterrouteasync)
4. [Pattern 2: Workflow Static Routing](#pattern-2-workflow-static-routing)
5. [Pattern 3: Dynamic Routing via Activity](#pattern-3-dynamic-routing-via-activity)
6. [Do's and Don'ts](#dos-and-donts)
7. [Agent Registry: Safe vs. Unsafe Contexts](#agent-registry-safe-vs-unsafe-contexts)
8. [Choosing the Right Pattern](#choosing-the-right-pattern)
9. [References](#references)

---

## Overview

TemporalAgents supports multiple routing approaches. The simplest delegates routing to an LLM via `IAgentRouter`. For more control, you can route inside a Temporal workflow with hardcoded agent names. And when the set of available agents changes at runtime — feature flags, A/B tests, rolling deployments — you need the dynamic routing pattern that safely queries the agent registry inside an activity.

All three patterns produce **durable** routing decisions: once Temporal records which agent was chosen, that decision survives crashes and replays without re-evaluation.

---

## Three Routing Approaches

| Approach | Where routing happens | Registration | Use case |
|---|---|---|---|
| **IAgentRouter + RouteAsync** | External (`DefaultTemporalAgentClient`) | `SetRouterAgent` + `AddAgentDescriptor` | External callers, simple classification |
| **Workflow static routing** | Inside `[Workflow]` with hardcoded agent names | Just `AddAIAgent` | Conditional logic, fallback chains, multi-step classification |
| **Workflow dynamic routing** | Inside `[Workflow]` via activity that queries descriptors | `AddAIAgent` + `AddAgentDescriptor` + custom activity | Agent set changes at runtime, feature flags, A/B testing |

---

## Pattern 1: IAgentRouter + RouteAsync

### How It Works

`AIAgentRouter` is the built-in `IAgentRouter` implementation. It sends a compact prompt to an LLM — listing registered agent names and descriptions — and asks the model to respond with the single best-matching agent name. The response is parsed with an exact match first, then a fuzzy (case-insensitive substring) fallback.

### Registration

```csharp
// Agents carry their descriptions via AsAIAgent(description: ...),
// which are auto-extracted into the descriptor registry on AddAIAgent().
var weatherAgent = chatClient.AsAIAgent(
    name: "WeatherAgent",
    description: "Handles weather forecasts and conditions.",
    instructions: "You are a weather specialist...");

var billingAgent = chatClient.AsAIAgent(
    name: "BillingAgent",
    description: "Handles billing inquiries and payment issues.",
    instructions: "You are a billing specialist...");

services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        // Descriptions are auto-extracted — no AddAgentDescriptor() needed
        opts.AddAIAgent(weatherAgent);
        opts.AddAIAgent(billingAgent);

        // Registers AIAgentRouter as IAgentRouter automatically
        opts.SetRouterAgent(routerAgent);
    });
```

> **Note:** `AddAgentDescriptor()` is still available for factory-registered agents (`AddAIAgentFactory`) or to explicitly override an auto-extracted description.

### Usage

```csharp
var response = await agentClient.RouteAsync(sessionKey, new RunRequest(userMessage));
```

The `DefaultTemporalAgentClient` calls `IAgentRouter.RouteAsync` to pick the agent, then forwards the message to that agent's workflow.

### Matching Behavior

1. **Exact match** — the LLM response equals a registered name (most common case)
2. **Fuzzy fallback** — exactly one registered name appears as a substring in the response (logged as a warning)
3. **Ambiguous** — multiple names found in the response text: throws `InvalidOperationException`
4. **Unrecognized** — no match at all: throws `InvalidOperationException`

### Pros and Cons

| Pros | Cons |
|------|------|
| Simplest setup, no custom workflow needed | Less control over routing logic |
| Routing prompt is built automatically from descriptors | LLM-dependent — may misclassify |
| Fuzzy fallback tolerates minor formatting variation | No fallback chain or multi-step classification |

### Reference

See `samples/MultiAgentRouting/` for a complete working example.

---

## Pattern 2: Workflow Static Routing

### How It Works

A classifier agent runs as the first step inside a Temporal workflow. The workflow inspects the classifier's output and uses `GetAgent("name")` with hardcoded agent names to dispatch to the right specialist.

### Registration

No `SetRouterAgent` or `AddAgentDescriptor` needed — just register the agents:

```csharp
services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(classifierAgent);
        opts.AddAIAgent(ordersAgent);
        opts.AddAIAgent(supportAgent);
        opts.AddAIAgent(generalAgent);
    });
```

### Usage

```csharp
[Workflow("CustomerServiceWorkflow")]
public class CustomerServiceWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // Step 1: Classify the intent
        var classifier = TemporalWorkflowExtensions.GetAgent("Classifier");
        var session = await classifier.CreateSessionAsync();
        var classification = (await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], session))
            .Text?.Trim().ToUpperInvariant();

        // Step 2: Route based on classification
        var specialistName = classification switch
        {
            "ORDERS" => "OrdersAgent",
            "TECH_SUPPORT" => "SupportAgent",
            _ => "GeneralAgent",  // always provide a fallback
        };

        // Step 3: Call the specialist
        var specialist = TemporalWorkflowExtensions.GetAgent(specialistName);
        var specialistSession = await specialist.CreateSessionAsync();
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], specialistSession);

        return response.Text ?? string.Empty;
    }
}
```

### Pros and Cons

| Pros | Cons |
|------|------|
| Full control over routing logic | Agent names hardcoded in workflow code |
| Durable routing decisions (recorded in history) | Requires a custom workflow |
| Composable with fallback chains and multi-step logic | Adding new agents requires code changes + deployment |

### Reference

See `samples/WorkflowOrchestration/` for workflow-based agent orchestration patterns.

---

## Pattern 3: Dynamic Routing via Activity

This is the most flexible pattern — and the one that requires the most care to get right.

### The Problem

You want routing decisions to depend on which agents are currently registered. Maybe you are:

- Rolling out `OrdersAgentV2` behind a feature flag
- A/B testing two different support agents
- Adding or removing agents at runtime without redeploying the workflow

The natural instinct is to call `TemporalAgentsOptions.GetRegisteredAgentNames()` or `TemporalAgentsOptions.IsAgentRegistered()` directly in workflow code. **This is unsafe.** Here is why:

- Workflow code must be **deterministic during replay**
- If agent registration changes between the original execution and replay, the routing decision would differ
- A different decision means a different code path, which means Temporal raises a **non-determinism error** and the workflow fails
- This is the same fundamental reason `DateTime.UtcNow` and `Guid.NewGuid()` are forbidden in workflows

### The Safe Pattern: Descriptors + Activity

The key insight: `AddAgentDescriptor()` stores `(Name, Description)` pairs in `TemporalAgentsOptions`. While its primary consumer is `AIAgentRouter`, the descriptors are also available via `options.GetRegisteredDescriptors()` — making them a general-purpose agent metadata store that any activity can query.

The workflow discovers available agents by reading descriptors inside an activity, then passes them to the Classifier as context. No hardcoded agent names in the routing logic at all.

#### Step 1: Register agents with descriptions (no `SetRouterAgent`)

```csharp
// Specialist agents carry descriptions via AsAIAgent(description: ...)
var ordersAgent = chatClient.AsAIAgent(
    name: "OrdersAgent",
    description: "Handles order tracking, returns, and shipping.",
    instructions: "You are an orders specialist...");

// ... similarly for supportAgent, generalAgent

services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(classifierAgent);  // No description — not routable
        opts.AddAIAgent(ordersAgent);      // Description auto-extracted
        opts.AddAIAgent(supportAgent);
        opts.AddAIAgent(generalAgent);
    });
```

> Descriptions are auto-extracted from `AIAgent.Description` into the descriptor registry. For factory-registered agents, use `AddAgentDescriptor()` explicitly.

#### Step 2: Define routing activities

```csharp
public class RoutingActivities(TemporalAgentsOptions options)
{
    // Returns all registered descriptors — the workflow uses these to build the classifier prompt
    [Activity("GetAvailableAgents")]
    public AgentInfo[] GetAvailableAgents()
    {
        return options.GetRegisteredDescriptors()
            .Select(d => new AgentInfo(d.Name, d.Description))
            .ToArray();
    }

    // Validates an LLM-chosen name against the registry (LLMs can hallucinate)
    [Activity("ValidateAgent")]
    public string ValidateAgent(string agentName, string fallback)
    {
        return options.IsAgentRegistered(agentName) ? agentName : fallback;
    }
}

public record AgentInfo(string Name, string Description);
```

#### Step 3: Use in a workflow — zero hardcoded agent names

```csharp
[Workflow("DynamicRoutingWorkflow")]
public class DynamicRoutingWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // Step 1: Discover available agents via activity (cached on replay)
        var agents = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.GetAvailableAgents(),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        // Step 2: Build a dynamic routing prompt from discovered descriptors
        var agentList = string.Join("\n", agents.Select(a => $"  {a.Name} — {a.Description}"));
        var routingPrompt =
            $"Given the user question, respond with ONLY the best-matching agent name.\n\n" +
            $"Available agents:\n{agentList}\n\nUser question: {userQuestion}";

        var classifier = TemporalWorkflowExtensions.GetAgent("Classifier");
        var session = await classifier.CreateSessionAsync();
        var chosenAgent = (await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, routingPrompt)], session)).Text?.Trim();

        // Step 3: Validate the LLM's choice via activity (cached on replay)
        var agentName = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.ValidateAgent(chosenAgent!, "GeneralAgent"),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        // Step 4: Call the resolved agent
        var specialist = TemporalWorkflowExtensions.GetAgent(agentName);
        var specialistSession = await specialist.CreateSessionAsync();
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], specialistSession);

        return response.Text ?? string.Empty;
    }
}
```

### Why This Is Safe

Both activity results (descriptor list + validated agent name) are recorded in Temporal's event history. On replay, cached results are returned — the registry is never re-queried:

```
Original execution:
  Activity("GetAvailableAgents")  → reads registry → ["OrdersAgent", "SupportAgent", ...] → cached
  Activity("ValidateAgent")       → confirms "OrdersAgent" exists → cached

Replay after crash / new deployment:
  Activity("GetAvailableAgents")  → returns cached list (registry NOT queried)
  Activity("ValidateAgent")       → returns cached "OrdersAgent" (registry NOT queried)
```

### `AddAgentDescriptor` Without `SetRouterAgent`

A common misconception: `AddAgentDescriptor` only works with `IAgentRouter`. In reality, descriptors are stored in `TemporalAgentsOptions` and exposed via `GetRegisteredDescriptors()` — any code with access to the options can read them. The dynamic routing pattern uses descriptors as a queryable metadata store without creating an `IAgentRouter`.

> **Since auto-extraction:** If your agents carry `description:` in their `AsAIAgent()` calls, descriptors are populated automatically when you call `AddAIAgent()`. You only need explicit `AddAgentDescriptor()` for factory-registered agents or to override an auto-extracted value.

### Pros and Cons

| Pros | Cons |
|------|------|
| Zero hardcoded agent names in routing workflow | Most complex setup |
| Add/remove agents by changing registration, not workflow code | Requires custom activities + workflow |
| Feature flags and A/B testing are natural | Two extra activity calls (discovery + validation) |
| Descriptors provide rich context for LLM classification | Descriptors must be kept in sync with agent registrations |

---

## Do's and Don'ts

### DO

- Use `GetAgent("name")` with string constants or activity results inside workflows
- Query `TemporalAgentsOptions` inside activities (they are not replayed)
- Provide a default/fallback agent for unrecognized classifications
- Use `AddAgentDescriptor` for `IAgentRouter` routing prompts AND as a queryable metadata store for dynamic routing via activities
- Test routing with edge cases: empty LLM response, unexpected classification, ambiguous matches
- Catch `InvalidOperationException` from `IAgentRouter` when the LLM returns an unrecognized name

### DON'T

- Don't call `TemporalAgentsOptions.GetRegisteredAgentNames()` inside a `[Workflow]` class — non-deterministic on replay
- Don't call `TemporalAgentsOptions.IsAgentRegistered()` inside a `[Workflow]` class — same reason
- Don't use `IAgentRouter` AND workflow-based routing for the same message — pick one approach
- Don't forget the `_` default case in switch expressions — LLMs produce unexpected output
- Don't assume `IAgentRouter` will always return a valid name — handle `InvalidOperationException`

---

## Agent Registry: Safe vs. Unsafe Contexts

| Context | Can query registry? | Why |
|---|---|---|
| `Program.cs` / startup | Yes | Runs once at startup, not replayed |
| Health-check endpoint | Yes | External HTTP handler, not workflow code |
| Activity code | Yes | Activities return cached results on replay |
| `[Workflow]` class method | **NO** | Replayed deterministically — registry may have changed |
| `[WorkflowUpdate]` handler | **NO** | Part of workflow code, subject to replay |
| `[WorkflowQuery]` handler | Read-only is safe but pointless | Queries don't affect workflow state |

The rule is simple: **if the code runs inside a workflow execution context, do not query the registry.** Wrap the query in an activity instead.

---

## Choosing the Right Pattern

Use this decision tree to pick the right routing approach:

**Is the agent set fixed at compile time?**

- **Yes** -- Do you need custom routing logic (fallbacks, multi-step, confidence thresholds)?
  - **Yes** -- Use **Pattern 2** (static workflow routing)
  - **No** -- Use **Pattern 1** (`IAgentRouter` + `RouteAsync`) for the simplest setup
- **No** (agents change at runtime: feature flags, deployments, A/B tests) -- Use **Pattern 3** (dynamic routing via activity)

**Are callers external (API endpoints, CLI, console app)?**

- **Yes, and simple routing is fine** -- Use **Pattern 1** (`IAgentRouter` + `RouteAsync`)
- **Yes, but you need workflow durability** -- Start a workflow that uses **Pattern 2** or **Pattern 3** internally

**Do you need fallback chains or multi-step classification?**

- **Yes** -- Use **Pattern 2** or **Pattern 3** (workflow-based patterns give full control)
- **No** -- Use **Pattern 1** (`IAgentRouter` handles single-step classification automatically)

---

## References

- `samples/MultiAgentRouting/` — Pattern 1 example (LLM-powered routing with `IAgentRouter`)
- `samples/WorkflowRouting/` — Pattern 2 (static) and Pattern 3 (dynamic) examples
- [`durability-and-determinism.md`](../architecture/MAF/durability-and-determinism.md) — Why workflow code must be deterministic
- [`agent-sessions-and-workflow-loop.md`](../architecture/MAF/agent-sessions-and-workflow-loop.md) — How agent calls become durable activities
- [`session-statebag-and-context-providers.md`](../architecture/MAF/session-statebag-and-context-providers.md) — StateBag and AIContextProvider integration
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — Agent registry API (`GetRegisteredAgentNames`, `IsAgentRegistered`)
- `src/Temporalio.Extensions.Agents/AIAgentRouter.cs` — Built-in LLM router implementation
- `src/Temporalio.Extensions.Agents/IAgentRouter.cs` — Router interface

---

_Last updated: 2026-03-11_
