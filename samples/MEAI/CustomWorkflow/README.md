# CustomWorkflow: Domain-Typed Output from a Durable Chat Workflow

## Overview

This sample shows how to subclass `DurableChatWorkflowBase<TOutput>` to build a custom durable
chat workflow that returns domain-specific typed output alongside the LLM response. The shopping
assistant workflow extends the base class with `ShoppingTurnOutput`, which bundles the `ChatResponse`
with a list of `CartAction` records captured during tool calls in that turn. Use this pattern when
the stock `DurableChatWorkflow` / `DurableChatSessionClient` return type is too generic for your domain.

- `DurableChatWorkflowBase<TOutput>` — abstract base class providing history, continue-as-new, shutdown
- `ShoppingAssistantWorkflow` — concrete subclass with a `[WorkflowUpdate("Shop")]` handler
- `ShoppingActivities` — custom activity class that injects cart tools and returns `ShoppingTurnOutput`
- `RegisterDefaultWorkflow = false` — skips registering `DurableChatWorkflow` so only the custom workflow is on the worker
- Callers use `handle.ExecuteUpdateAsync<ShoppingTurnOutput>("Shop", ...)` directly — no `DurableChatSessionClient`

## Architecture

```
Program.cs
    │
    ├─ StartWorkflowAsync(ShoppingAssistantWorkflow)
    │
    └─ handle.ExecuteUpdateAsync<ShoppingTurnOutput>("Shop", input)
           │
           └─ ShoppingAssistantWorkflow.ShopAsync()    [WorkflowUpdate("Shop")]
                  │
                  └─ Workflow.ExecuteActivityAsync(ShoppingActivities.GetShoppingResponseAsync)
                         │
                         ├─ IChatClient.GetResponseAsync(messages, {add_to_cart, remove_from_cart})
                         └─ returns ShoppingTurnOutput { Response, CartActions }
```

Note: cart tools execute inline inside the single `ShoppingActivity` invocation via `UseFunctionInvocation()`. They are NOT dispatched as separate Temporal activities. For per-tool activity dispatch with retry/timeout isolation, see `samples/MEAI/DurableTools/`.

## Highlights

- **Typed update responses.** `[WorkflowUpdate("Shop")]` returns `ShoppingTurnOutput`, carrying both the assistant's `ChatResponse` and the `IReadOnlyList<CartAction>` mutated during tool calls. The managed `DurableChatSessionClient.SendAsync` API returns `DurableSessionResponse`; use a custom workflow when the update must return additional domain data atomically.
- **Cart tools are intentionally activity-local.** `ShoppingActivities.GetShoppingResponseAsync` defines `add_to_cart` and `remove_from_cart` as local `AIFunction` instances that close over a per-invocation `List<CartAction>`, then passes them through `ChatOptions.Tools` to a client configured with `UseFunctionInvocation()`. This is valid only because this sample owns its activity and does not call the managed session client. It is not the managed durable-tool contract. For workflow-managed, per-tool activity dispatch, use `AddDurableTools` as shown in the `DurableTools` sample.
- **`RegisterDefaultWorkflow = false`.** Passing this option to `AddDurableAI` prevents the library from registering `DurableChatWorkflow` on the worker. This avoids a conflict when the custom workflow is registered instead, and signals intent clearly.
- **Base class handles history and continue-as-new.** `DurableChatWorkflowBase<TOutput>` manages conversation history accumulation, the idle TTL loop, continue-as-new transitions, HITL approval handlers, and the `RequestShutdownAsync` signal. The base's `RunAsync` is `protected virtual` and is **not** annotated with `[WorkflowRun]` (see `DurableChatWorkflowBase.cs` line 183), so the subclass must declare its own `[WorkflowRun] public new Task RunAsync(DurableChatWorkflowInput input)` that delegates to `base.RunAsync(input)`. Beyond that, the subclass implements the three abstract members — `ExecuteTurnAsync`, `BuildResponseEntry`, and `CreateContinueAsNewException` — and calls `RunTurnAsync` from its own `[WorkflowUpdate]` handler.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key (`OPENAI_API_KEY`)
- An OpenAI-compatible base URL (`OPENAI_API_BASE_URL`) — required; `Program.cs` throws `InvalidOperationException` if missing
- Optionally, `OPENAI_MODEL` to override the default (`gpt-4o-mini`)

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/CustomWorkflow
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/CustomWorkflow
# Optional:
# dotnet user-secrets set "OPENAI_MODEL" "gpt-4o-mini" --project samples/MEAI/CustomWorkflow
```

### Run

```bash
dotnet run --project samples/MEAI/CustomWorkflow/CustomWorkflow.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 Demo: Custom Workflow Output (ShoppingAssistant)
════════════════════════════════════════════════════════
 Session ID: shopping-<guid>

 Turn 1 — Add to cart
   Assistant: I've added 1x Blue Widget (SKU-001) to your cart.
   Cart actions:
     [ADD] Blue Widget (SKU: SKU-001) x1

 Turn 2 — Remove from cart
   Assistant: I've removed the Blue Widget (SKU-001) from your cart.
   Cart actions:
     [REMOVE] Blue Widget (SKU: SKU-001)

════════════════════════════════════════════════════════

Done.
```
