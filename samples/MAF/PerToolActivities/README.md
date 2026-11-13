# PerToolActivities — Serial Tool Activities

This sample demonstrates **per-tool Temporal activities** in
`TemporalCommunity.Extensions.Agents`. Every LLM call is its own
`RunDurableAgentStep` activity, and every model-requested tool call is its own
`InvokeAgentTool` activity. Calls in one model response execute serially in model
order. Tool activities get one attempt by default; the per-agent retry policy opts
all tools on that agent into retries. The worker-level retry policy applies to model and
context-preparation activities, not tools or interceptors.

For the conceptual background, see
[`docs/how-to/MAF/durable-agents.md`](../../../docs/how-to/MAF/durable-agents.md).

---

## The Story

A refund-handling agent receives a customer complaint and must perform three tool
calls to resolve it:

| Tool | Type | Default activity attempts | Why |
|---|---|---:|---|
| `lookup_order` | Read | **1** | Retries require an explicit agent policy |
| `apply_refund` | Write | **1** | Avoids automatic duplicate refunds after uncertain completion |
| `send_email` | Write | **1** | Avoids automatic duplicate delivery after uncertain completion |

The same workflow also demonstrates all four per-turn selection states:

| Run option | Meaning |
|---|---|
| `EnableToolCalls = true`, `EnableToolNames = null` | all registered tools |
| `EnableToolNames = ["lookup_order"]` | only the named registered subset |
| `EnableToolNames = []` | no tools |
| `EnableToolCalls = false` | no tools, regardless of the name list |

Five scenarios run:

1. **Happy path** — an explicitly approved refund is looked up, applied, and emailed once each.
2. **Subset** — only `lookup_order` is exposed and dispatchable.
3. **Empty list** — no tools are exposed or dispatchable.
4. **Disabled** — all model-returned tool calls are blocked before durable dispatch.
5. **Lookup failure** — the in-memory `lookup_order` tool throws on its first
   invocation. The default one-attempt policy surfaces the activity failure
   instead of retrying it or dispatching later calls from the response.

This scenario shows the retry boundary, not exactly-once behavior. If a failed
write activity may have completed an external effect, reconcile with the system
of record before manually retrying.

`RefundWorkflow` excludes `DurableTurnFailedException` and
`DurableTurnOutcomeTooLargeException` from its outer invocation activity's retry policy.
The failed turn's request ID is retained, so retrying the proxy would retrieve the same failure,
not run the lookup again. Other invocation failures can still retry with the same IDs.
Each scenario checks its tool counters, including no writes in the lookup-only case and no tool
calls when tools are disabled or the enabled-name list is empty. Scenario 5 expects the terminal
failure above. Unexpected failures or counts produce a nonzero process exit code and no completion
marker; model prose alone is not evidence that a refund or email tool executed.

---

## Prerequisites

- .NET 10 SDK
- Temporal Service 1.31.0 or newer (local: `temporal server start-dev --namespace default --search-attribute AgentName=Keyword --search-attribute SessionCreatedAt=Datetime --search-attribute TurnCount=Int`)
- An OpenAI API key:
  ```bash
  dotnet user-secrets set "OPENAI_API_KEY" "sk-..." \
      --project samples/MAF/PerToolActivities
  ```
  (Optional `OPENAI_API_BASE_URL` if you target a self-hosted compatible endpoint.)

---

## Run

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj
```

After the scenarios complete, open the Temporal Web UI at
<http://localhost:8233> and inspect either workflow's history. Each
`RunDurableAgentStep` and each `InvokeAgentTool` shows up as a distinct activity
row with the tool name in its summary — clicking into the failed `lookup_order`
row in the failure scenario shows one attempt. Tool activities are serial: a
later call is not scheduled until the previous call completes.

---

## Expected Output (abridged)

```
Worker started. Running serial tool activity scenarios...

─── Scenario 1: Happy path (all tools succeed) ──────────────
  Workflow:  refund-happy-...
  Complaint: I never received my order ORD-002 and want a full $49.99 refund...
  Agent:     I have refunded $49.99 to your order ORD-002 and emailed confirmation to acme@example.com.
  Final state → LookupCalls = 1, RefundCalls = 1, SendCalls = 1

─── Scenario 2: Subset (lookup_order only) ────────────────
  Selection: EnableToolCalls=True, EnableToolNames=[LOOKUP_ORDER]
  ...

─── Scenario 3: Empty enabled-name list (no tools) ─────────
  Selection: EnableToolCalls=True, EnableToolNames=[]
  ...

─── Scenario 4: Tool calls disabled ────────────────────────
  Selection: EnableToolCalls=False, EnableToolNames=<all registered>
  ...

─── Scenario 5: Lookup failure (one attempt by default) ──────
  Workflow:  refund-retry-...
  Complaint: Refund my order ORD-001 for $19.99. Email confirmation to acme@example.com.
  Expected terminal turn failure: Activity task failed
  This run    → LookupCalls = 1, RefundCalls = 0, SendCalls = 0
  Cumulative  → LookupCalls = 3, RefundCalls = 1, SendCalls = 1
  ✓ Default policy confirmed: lookup ran once; later tools were not dispatched after its failure.

─── View the activity timeline ──────────────────────────────
  Open http://localhost:8233 in the Temporal Web UI.
  ...

Done.
PerToolActivities sample complete.
```

The exact wording of the agent's reply varies between runs — the model is
non-deterministic. A timeout or lost activity result can leave an external
effect's outcome uncertain. Use downstream idempotency and reconcile with the
system of record before retrying an ambiguous write.

On a turn failure after dispatch, the shared workflow keeps a bounded interrupted-turn projection
for later model context: completed tool results remain paired with their calls, uncertain outcomes
are marked unknown, and undispatched calls are marked not executed. This is not an external-effect
receipt; reconcile with the system of record before retrying a write.

Selection is enforced twice: the model receives only the selected tool declarations, and the
workflow checks every returned function call again before interceptor, approval, or tool activity
dispatch. Unknown and excluded names receive the same generic result. Selection is exposure
control, not authorization; write tools must still authorize current application identity and
resource state inside their activity.

---

## How It Wires Together

### One registration call — `AddDurableAgent`

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());
builder.Services.AddTemporalClient("localhost:7233", "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Instructions = "You are a customer refund specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 10;

            // Read tool — one activity attempt by default.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools — one activity attempt by default; retry is configured
            // at agent level only.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    "apply_refund"),
                opts => opts.WithTimeout(TimeSpan.FromSeconds(30)));

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    "send_email"),
                opts => opts.WithTimeout(TimeSpan.FromSeconds(30)));
        });
    })
    .AddWorkflow<RefundWorkflow>();
```

What this design simplifies away:

- **No `BuildServiceProvider()` bootstrap** — the `AddTool(string, sp => ...)`
  factory runs at first activity dispatch with the worker's runtime
  `IServiceProvider`. Tool services resolve cleanly without an early DI build.
- **No `AddDurableAI(_ => { })`** + **no `AddDurableTools(...)`** — the durable
  agent path subsumes both. (MEAI's `DurableChatWorkflow` users still need them.)
- **No `EnablePerToolActivities = true`** — every `AddDurableAgent` is durable
  by definition.
- **No per-tool retry override** — agent-level retry policy is the only retry opt-in.
- **No inline function-invocation middleware** — register a bare `IChatClient`.
  `UseProvidedChatClientAsIs = true` prevents MAF from adding its own middleware, but a
  caller-installed `UseFunctionInvocation()` loop is incompatible with the workflow-owned
  `InvokeAgentTool` activities and is rejected.

### `DurableToolOptions` fluent sugar

| Method                       | What it does                                           |
| ---------------------------- | ------------------------------------------------------ |
| `opts.WithTimeout(t)`        | Sets `StartToCloseTimeout`                              |

Tool retries are controlled by the agent's `RetryPolicy` and apply to all its tools.

---

## What You'll See in the Web UI

Scenario 1 (happy path) workflow history:

```
RunDurableAgentStep                                  ← LLM call #1 → returns lookup_order
InvokeAgentTool:RefundAgent:lookup_order             ← 1 attempt, succeeds
RunDurableAgentStep                                  ← LLM call #2 → returns apply_refund
InvokeAgentTool:RefundAgent:apply_refund             ← 1 attempt, succeeds (MaximumAttempts = 1)
RunDurableAgentStep                                  ← LLM call #3 → returns send_email
InvokeAgentTool:RefundAgent:send_email               ← 1 attempt, succeeds (MaximumAttempts = 1)
RunDurableAgentStep                                  ← LLM call #4 → final assistant message
```

Scenario 5 (lookup failure) — the failure surfaces after one activity attempt:

```
InvokeAgentTool:RefundAgent:lookup_order             ← 1 attempt, fails
```

Because dispatch is serial, later tool calls from the same model response are
not scheduled after this failure.

---

## See Also

- Conceptual guide: [`docs/how-to/MAF/durable-agents.md`](../../../docs/how-to/MAF/durable-agents.md)
- Workflow loop architecture: [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../../docs/architecture/MAF/agent-sessions-and-workflow-loop.md)
- The MEAI counterpart for tool durability: [`samples/MEAI/DurableTools/`](../../MEAI/DurableTools/)
