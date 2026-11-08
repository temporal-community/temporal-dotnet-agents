# Intercepting LLM Calls in Durable Agents

How to get in front of the model call a durable agent makes — to log prompts and responses, time
requests, capture token usage, emit custom telemetry, or rewrite payloads in flight.

**The short answer:** return a decorated `IChatClient` from `agent.ChatClient`, and put your logic in
`GetResponseAsync`. Every durable LLM call flows through it.

The rest of this page covers which of the two interception layers you want, the one trap that makes
decorators look broken, and how to diagnose it when they do.

---

## Pick your layer

There are two places to intercept, plus a third mechanism people often arrive here looking for.

| I want to see… | Use | Covered |
|---|---|---|
| Raw model transport — request, complete response, tokens, finish reason, provider payload | Decorate the `IChatClient` returned from `agent.ChatClient` | [below](#the-chat-client-layer) |
| One event wrapping a model-step attempt, with the durable session in hand | `agent.ConfigureAgentPipeline` with a `DelegatingAIAgent` | [below](#the-agent-middleware-layer) |
| Tool names, arguments, or policy before workflow dispatch and approval | `IAgentToolInterceptor` | [tool-interceptor.md](./tool-interceptor.md) |
| Observe, short-circuit, or transform a tool result after required approval | `TemporalAgentOptions.ToolMiddleware` | [below](#tool-middleware) |

**Both layers observe the same effective request values.** Managed sessions prepare provider context
once before the model/tool loop and record it as an activity result. Each model-step activity then
uses that snapshot to build its effective `ChatOptions`. Neither layer sees a pre-provider view:

```
PrepareAgentTurn activity → recorded provider context and StateBag delta
RunDurableAgentStep activity (one per model attempt)
├─ resolves the chat client and tool declarations from a per-attempt DI scope
├─ uses recorded provider context and the frozen tool manifest
├─ builds effective ChatOptions / ChatClientAgentRunOptions
└─ agent.RunAsync(augmentedMessages, session, runOptions, ct)
     └─ your ConfigureAgentPipeline middleware   ← outermost; receives the restored durable session
         └─ TemporalSessionBoundaryAgent         ← library-owned session boundary
             └─ ChatClientAgent                  ← constructed with AIContextProviders = null
                 └─ your IChatClient decorator   ← provider-level request and response
                     └─ provider client (OpenAI, Azure, …)
```

`ChatClientAgent` does **not** run the providers in this library. It is deliberately constructed with
`AIContextProviders = null`, because the preparation activity already executed them. Provider
output is recorded at the logical-turn boundary rather than recalled inside each model step.

So the choice between layers is not "before or after providers." It is:

- **Agent middleware** wraps the agent run for the step and has the restored `TemporalAgentSession`.
  Use it for model-step spans and retry-safe StateBag work, not a once-per-logical-turn callback.
- **Chat-client decorator** sits at the transport boundary. Use it for request/response telemetry,
  token usage, finish reason, or provider metadata.

---

## The chat-client layer

### Durable calls use `GetResponseAsync`

This is the single thing most likely to cost you an afternoon.

The durable path invokes the model through non-streaming `agent.RunAsync(...)`
(`AgentActivities.cs`, in `RunDurableAgentStepAsync`). Your `IChatClient` therefore sees
**`GetResponseAsync`**. Streaming through MAF `RunStreamingAsync` is not supported.

Put your telemetry in `GetResponseAsync`. A decorator that only overrides
`GetStreamingResponseAsync` does not observe durable model calls. The returned `ChatResponse`
contains the complete response, including usage and provider metadata exposed by MEAI.

### A logging decorator

```csharp
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

internal sealed class AuditingChatClient(IChatClient inner, ILogger<AuditingChatClient> logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // `messages` carries user content and system instructions — redact before shipping this
        // anywhere you would not put PII.
        logger.LogInformation(
            "LLM request: messages={Count}, tools={ToolCount}",
            messages.Count(),
            options?.Tools?.Count ?? 0);

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "LLM response completed: duration={DurationMs}ms, input_tokens={Input}, " +
                "output_tokens={Output}, total_tokens={Total}, finish_reason={FinishReason}",
                stopwatch.ElapsedMilliseconds,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                response.Usage?.TotalTokenCount,
                response.FinishReason);
            return response;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("LLM call canceled after {DurationMs}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM call failed after {DurationMs}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
```

> **`options?.ModelId` is usually null.** The library clones `registration.ChatOptions` and never
> sets `ModelId`; the canonical registration pins the model on the provider client instead (see
> `samples/MAF/BasicAgent/Program.cs`). Logging it yields an empty field. Record the model where you
> configure the provider client, or set `ModelId` yourself on `agent.ChatOptions`.

### Registration

Build the decorator in the factory. One hop, and the lifetime matches how the library actually uses
it:

```csharp
opts.AddDurableAgent("Assistant", agent =>
{
    agent.Instructions = "You are a helpful assistant.";
    agent.ChatClient = sp => new AuditingChatClient(
        sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
        sp.GetRequiredService<ILogger<AuditingChatClient>>());
    agent.AddTool(weatherTool);
});
```

Registering the decorator as a DI **singleton** also works, and changes its lifetime:

| Registration | Lifetime |
|---|---|
| Built in the `agent.ChatClient` factory (above) | **Attempt-local** — a new instance per activity attempt |
| DI singleton | **Shared** — one instance across attempts, sessions, and concurrent activities on the worker |

A singleton must therefore be thread-safe. But the important point applies to both: **neither is
session-scoped.** An attempt-local instance is discarded when the attempt ends; a singleton is shared
by conversations that have nothing to do with each other. Per-conversation state belongs in neither —
see [Where state belongs](#where-state-belongs).

No caller-side change is needed. Invoke the agent as usual:

```csharp
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("What's the weather?", session);
```

### Before you hand-roll one

`Microsoft.Extensions.AI` already ships `UseLogging()` and `UseOpenTelemetry()` on
`ChatClientBuilder`. If you want standard request/response logging or OTel spans rather than a custom
shape, use those instead of the class above:

```csharp
agent.ChatClient = sp => sp.GetRequiredService<OpenAIClient>()
    .GetChatClient("gpt-4o-mini")
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "MyApp.Llm")
    .Build();
```

> **Never call `.UseFunctionInvocation()` on this chain.** The workflow owns the tool-dispatch loop —
> each tool call is its own `InvokeAgentTool` activity. A `FunctionInvokingChatClient` in your chat
> client would run tools **in-process inside the LLM activity instead**, and per-tool durability,
> retry policies, and timeouts would stop applying.
>
> The library rejects this. On every activity attempt, before the model is called, the chat client
> is inspected and the activity fails **non-retryably** — a misconfiguration cannot be fixed by
> trying again, so it must not consume the retry budget.
>
> A workflow caller sees:
>
> ```
> WorkflowFailedException
> └─ ActivityFailureException
>    └─ ApplicationFailureException   ErrorType = "DurableConfigurationException"
>       └─ ApplicationFailureException   ErrorType = "DurableFunctionInvocationConflictException"
> ```
>
> The typed `DurableFunctionInvocationConflictException` exists only inside the worker. Temporal's
> failure converter turns it into the nested `ApplicationFailureException` above, so a remote caller
> never receives that CLR type — match on `ErrorType`, not on the exception class.
>
> Rejection is **unconditional**, including for an agent with no registered tools:
> `FunctionInvokingChatClient.AdditionalTools` is consulted for tools that were not sent on the
> request, so a tool-less agent can still execute functions in-process.
>
> If you need an in-process tool loop for other, non-durable work, register a separate decorated
> client for it and give the durable agent an undecorated one.

---

## The agent-middleware layer

To wrap the MAF agent call inside each model-step attempt, set `ConfigureAgentPipeline`
(`Action<AIAgentBuilder>`). Per-agent, or `DefaultConfigureAgentPipeline` on the options for every
agent; the per-agent value wins when both are set.

```csharp
opts.DefaultConfigureAgentPipeline = pipeline =>
    pipeline.UseOpenTelemetry(agentTelemetrySource);
```

That exact line is live in `samples/MAF/MultiAgentRouting/Program.cs`.

### Writing custom middleware

The wrapper must derive from `DelegatingAIAgent`, preserve the exact supplied `inner` through
`base(inner)`, and delegate the non-streaming run:

```csharp
sealed class TimingAgent(AIAgent inner, ILogger<TimingAgent> logger)
    : DelegatingAIAgent(inner)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await base.RunCoreAsync(messages, session, options, cancellationToken);
        }
        finally
        {
            logger.LogInformation("Agent run completed in {Elapsed}",
                Stopwatch.GetElapsedTime(started));
        }
    }

}

agent.ConfigureAgentPipeline = pipeline =>
    pipeline.Use((inner, services) =>
        new TimingAgent(inner, services.GetRequiredService<ILogger<TimingAgent>>()));
```

The durable agent calls `RunAsync`, so `RunCoreAsync` is the override that fires.

### Rules the library enforces

**Stay transparent.** Do not return a separate agent from the factory, and do not hide `inner`
inside a custom `AIAgent` subclass. Both are rejected, because the library cannot otherwise prove
its `ChatClientAgent` is still the durable model-call leaf. Request-level short-circuiting inside a
valid `DelegatingAIAgent` is supported for that step.

**Do not implement `IDisposable` / `IAsyncDisposable`.** The pinned MAF 1.23.0 builder API does
not expose whether a factory-created wrapper is owned by its factory or by DI, so the library
rejects that ambiguous shape. Put resource-owning dependencies in the activity DI scope. The one
exception is MAF's built-in
`OpenTelemetryAgent` — the library knows that wrapper owns its telemetry client and disposes it at
the end of each validation or activity build. If `AIAgentBuilder.Build` throws before returning a
root, MAF exposes no partially built wrappers, so the library cannot dispose what it cannot reach.

**Pass the exact session through.** Middleware receives the restored `TemporalAgentSession` and may
persist retry-safe state in its `StateBag`, but must hand that same object to `next`:

```csharp
sealed class AttemptCountingAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var durable = (TemporalAgentSession)session!;
        // Read/write a JSON-serializable StateBag value here. The activity persists the bag.

        return await base.RunCoreAsync(
            messages, durable, options, cancellationToken).ConfigureAwait(false);
    }
}
```

Passing `null` or substituting another session is rejected: only the original restored StateBag is
durably serialized. The innermost `ChatClientAgent` uses a separate transient
`ChatClientAgentSession` behind the library's own boundary; middleware that requires that
leaf-specific type is not supported.

### Lifetime

The pipeline callback runs **once during worker-startup validation** — so a broken pipeline fails at
boot rather than at first request — and **once per `RunDurableAgentStep` activity attempt**,
including retries. Preparation and completion activities also construct a pipeline to supply the
provider callback's agent; they do not call its `RunAsync`. Construction is therefore not evidence
that model middleware ran. All these builds use a DI scope, so factories may resolve scoped dependencies.
Treat wrapper fields as attempt-local, never session-local; durable state goes in the
[StateBag](#where-state-belongs).

---

## Tool middleware

Set `ToolMiddleware` inside `.Agent(...)`'s per-agent Temporal configuration callback. It uses MAF's
four-argument callback shape: agent, `FunctionInvocationContext`, `next`, cancellation token;
the result is `ValueTask<object?>`. See the complete, runnable
[Middleware sample](../../../samples/MAF/Middleware/) for registration and two turns without an API key.

The callback runs inside each individual tool activity, after required workflow approval.
`context.Arguments.Services` is that attempt's async-disposed DI scope. The workflow records the
approved function name, call ID, and canonical argument fingerprint before waiting; the activity
checks them before middleware and again before invoking the function. Argument changes or function
replacement after approval fail non-retryably. Pre-approval argument transformation by this
middleware is not supported.

Await `next(context, cancellationToken)` at most once. Returning a serializable result without
calling `next` is a supported short-circuit; transforming the result is also supported. Returning
before an asynchronous `next` finishes or swallowing a rejected invocation does not bypass the
guard. Result serialization and transport limits apply after middleware.

Set `context.Terminate = true` to end the logical turn after its result: remaining tool calls get
explicit skipped results and no further model round runs. Provider completion still runs.
The function is a declaration view; invoking it directly is rejected. The agent exposes registration
name/description, not an executable agent or proxy.

`Messages`, `Iteration`, `FunctionCallIndex`, and `FunctionCount` describe the initiating managed
session batch and must remain unchanged. `Options` is null; changing it, enabling streaming, or
replacing the supplied context/services is unsupported.

Callbacks and functions remain **at-least-once** on activity retry, including lost completion after
a successful effect. A completed activity result is replayed without rerunning middleware; a
same-RequestId turn retry also does not redispatch while its terminal outcome is retained.
Use downstream idempotency or reconciliation for effectful tools.

Do not install MAF's function-callback `AIAgentBuilder.Use(...)` on the model pipeline: it requires
an in-process function loop. `ToolMiddleware` is the durable per-tool boundary instead.

---

## Your code runs inside an activity, not the workflow

Model middleware executes inside `RunDurableAgentStep`, once **per activity attempt**.
Four consequences, all of which bite in production:

- **Retries re-run your code.** A retried LLM step re-invokes the factory and the decorator. Any
  non-idempotent side effect — billing rows, audit writes, counters — will duplicate.
- **If your decorator throws, the activity fails.** Temporal retries the model call, and you pay for
  the tokens again.
- **Decorator instance state is never session-scoped.** Whether it is attempt-local or a shared
  singleton depends on how you registered it — and neither tracks a conversation. Attempts retry,
  land on other workers, and run concurrently with other sessions.
- **There is no workflow-side interception point.** Nothing your decorator emits is recorded in
  workflow history, and you cannot decorate from workflow context.

The upside of the same boundary: because you are inside the activity, spans and logs you emit nest
naturally in the existing trace context, under the `agent.turn` span. See
[observability.md](./observability.md) for the full hierarchy.

### Where state belongs

If you need something to survive across LLM rounds or turns, do not put it in a decorator field.
Use the session StateBag, reachable from inside the activity:

```csharp
var session = TemporalAgentContext.Current.CurrentSession;
session.StateBag.SetValue("my.counter", value);
```

The StateBag is carried across steps and turns, and survives continue-as-new when the workflow
carries the session forward. That is the durability a decorator field cannot give you: an
attempt-local instance is discarded when the attempt ends, and a singleton outlives the conversation
while being shared with every other one on the worker.

---

## How it works

`AddDurableAgent` caches only a **blueprint** — frozen configuration, no live DI instances, no
constructed middleware, no chat client. Nothing composed is cached.

Each `RunDurableAgentStep` activity attempt then:

1. Opens a fresh DI scope and resolves `agent.ChatClient` from it, so scoped dependencies are legal
   in your decorator.
2. Restores recorded provider context for managed sessions and resolves registered tool declarations
   from the attempt's scope. Tool implementations resolve again in their own tool-activity scopes.
3. Builds a `ChatClientAgent` with `UseProvidedChatClientAsIs = true`, so MAF does **not** auto-inject
   `FunctionInvokingChatClient` — the workflow owns tool dispatch.
4. Applies your `ConfigureAgentPipeline` middleware and the library's session boundary around it.
5. Calls `agent.RunAsync(...)` with one freshly built `ChatOptions` for the step.
6. Disposes the pipeline at the end of the attempt.

The managed session workflow drives separate preparation, model, tool, and completion activities.
Client-side MAF middleware can wrap the keyed proxy itself, but runs in the caller's process:
its effects are not resumed after a client crash, and it is not worker-side model middleware.

### Composing with the library's per-step `ChatOptions`

The library builds one complete `ChatOptions` per step — cloned from `registration.ChatOptions`, then
stamped with `Instructions`, the selected `Tools`, and `ResponseFormat` from the active
`TemporalAgentRunOptions` and originating `RunRequest`. That value is the sole MAF run-options
channel; the library-created `ChatClientAgent` has no default `ChatOptions` to merge back in.

Agent middleware and chat-client decorators therefore observe the same effective values, with no
duplicated tools, instructions, or stop sequences. Per-request tool filtering
(`TemporalAgentRunOptions.EnableToolNames`) and response-format selection are applied to that
`ChatOptions` before the call — they need no decorator of your own.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| Decorator is constructed, but logs nothing | The durable path calls `GetResponseAsync`; a decorator that only overrides `GetStreamingResponseAsync` does not observe it. |
| `model=` is always empty | `options.ModelId` is not set by the library — the model is pinned on the provider client. |
| Usage and finish reason are always null | Read them from the complete `ChatResponse` returned by `GetResponseAsync`. |
| Duplicate log lines for one user message | Expected: one entry per LLM round, plus a fresh set per activity retry. |
| Activity fails once and does not retry; `ApplicationFailureException` with `ErrorType` `DurableConfigurationException` | `.UseFunctionInvocation()` is in your chat-client chain. The nested cause is an `ApplicationFailureException` with `ErrorType` `DurableFunctionInvocationConflictException`. Remove it — see the warning above. |
| Decorator never constructed at all | `agent.ChatClient` is returning a different client than the one you decorated. Check the factory, not the DI registration. |
| You want tool names and arguments, but see serialized `FunctionCallContent` | Wrong layer — use [`IAgentToolInterceptor`](./tool-interceptor.md). |

---

## Detection boundary

The check walks `DelegatingChatClient.InnerClient` and then falls back to
`GetService(typeof(FunctionInvokingChatClient))` — a non-null result there is treated as evidence
that the client participates in the effective pipeline. Since `FunctionInvokingChatClient` derives
from `DelegatingChatClient`, any chain built by convention is caught by one path or the other.

A wrapper that neither derives from `DelegatingChatClient` nor forwards `GetService` to its inner
client is **not** detectable. The guard is a backstop for the rule, not a substitute for following
it.

---

## Not applicable to

Both layers assume a `ChatClientAgent`-backed durable agent, which is what `AddDurableAgent` builds.
`A2AAgent`, graph-workflow agents, and other `AIAgent` subtypes have no inner `IChatClient` to wrap
and are not produced by `AddDurableAgent`. Instrument those at their own dispatch layer instead —
HTTP-client middleware for A2A, the OpenTelemetry source for graph workflows.

This page is also not the answer to **per-tool durability**. That is built in: every
`agent.AddTool(...)` already runs as its own `InvokeAgentTool` activity with its own retry policy and
timeout, configured via `DurableToolOptions`. See [durable-agents.md](./durable-agents.md).

---

## References

- [`observability.md`](./observability.md) — the full OTel span hierarchy; your spans nest under `agent.turn`.
- [`tool-interceptor.md`](./tool-interceptor.md) — intercepting and gating tool calls.
- [`durable-agents.md`](./durable-agents.md) — per-tool dispatch, retry, and timeout configuration.
- `samples/MAF/MultiAgentRouting/Program.cs` — live `DefaultConfigureAgentPipeline` with OpenTelemetry.
- `samples/MAF/BasicAgent/Program.cs` — canonical `agent.ChatClient = sp => ...` registration.
- `src/TemporalCommunity.Extensions.Agents/Workflows/AgentActivities.cs` — `RunDurableAgentStepAsync` and `BuildLiveAgentPipeline`, where the per-step `ChatOptions` is built and the agent is invoked.
- [`Microsoft.Extensions.AI.DelegatingChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.delegatingchatclient) — base class for chat-client decorators.
