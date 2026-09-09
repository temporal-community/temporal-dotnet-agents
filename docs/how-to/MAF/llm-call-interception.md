# Intercepting LLM Calls in Durable Agents

How to get in front of the model call a durable agent makes — to log prompts and responses, time
requests, capture token usage, emit custom telemetry, or rewrite payloads in flight.

**The short answer:** return a decorated `IChatClient` from `agent.ChatClient`, and put your logic in
`GetStreamingResponseAsync`. Every LLM call flows through it.

The rest of this page covers which of the two interception layers you want, the one trap that makes
decorators look broken, and how to diagnose it when they do.

---

## Pick your layer

There are two places to intercept, plus a third mechanism people often arrive here looking for.

| I want to see… | Use | Covered |
|---|---|---|
| Raw model transport — streaming updates as they arrive, tokens, finish reason, provider payload | Decorate the `IChatClient` returned from `agent.ChatClient` | [below](#the-chat-client-layer) |
| One event wrapping the whole agent run for the step, with the durable session in hand | `agent.ConfigureAgentPipeline` with a `DelegatingAIAgent` | [below](#the-agent-middleware-layer) |
| Tool names, arguments, or a gate before a tool runs | `IAgentToolInterceptor` | [tool-interceptor.md](./tool-interceptor.md) |

**Both layers observe the same effective request values.** Context providers run, and the effective
`ChatOptions` is built, *in the activity* — before the agent pipeline is invoked at all. Neither
layer sees a pre-provider view:

```
RunDurableAgentStep activity
├─ resolves the chat client and context providers from a per-attempt DI scope
├─ invokes the context providers, chaining each one's output into the next
├─ builds the augmented messages and the effective ChatOptions / ChatClientAgentRunOptions
└─ agent.RunStreamingAsync(augmentedMessages, session, runOptions, ct)
     └─ your ConfigureAgentPipeline middleware   ← outermost; receives the restored durable session
         └─ TemporalSessionBoundaryAgent         ← library-owned session boundary
             └─ ChatClientAgent                  ← constructed with AIContextProviders = null
                 └─ your IChatClient decorator   ← provider-level transport and streaming updates
                     └─ provider client (OpenAI, Azure, …)
```

`ChatClientAgent` does **not** run the providers in this library. It is deliberately constructed with
`AIContextProviders = null`, because the activity has already executed them — it replicates what
MAF's `PrepareSessionAndMessagesAsync` would otherwise do, so that provider output is captured in
the durable step rather than hidden inside the agent.

So the choice between layers is not "before or after providers." It is:

- **Agent middleware** wraps the agent run for the step and has the restored `TemporalAgentSession`.
  Use it for run-level spans, and for retry-safe StateBag work.
- **Chat-client decorator** sits at the transport boundary. Use it when you need the streaming
  updates themselves, token usage, finish reason, or anything about how the provider call behaved.

---

## The chat-client layer

### Only `GetStreamingResponseAsync` is ever called

This is the single thing most likely to cost you an afternoon.

The durable path invokes the model through `agent.RunStreamingAsync(...)`
(`AgentActivities.cs`, in `RunDurableAgentStepAsync`). Your `IChatClient` therefore only ever sees
**`GetStreamingResponseAsync`**. A decorator that puts its logging in `GetResponseAsync` is
constructed, is wired correctly, and logs nothing — which reads exactly like "my decorator isn't
running."

Put your telemetry in the streaming override. Override `GetResponseAsync` too if you like, for
direct callers, but do not rely on it here.

### A logging decorator

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

internal sealed class LoggingChatClient(IChatClient inner, ILogger<LoggingChatClient> logger)
    : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();

        // Pessimistic default: if we never reach the end of the stream and nothing threw, the
        // consumer stopped enumerating.
        var outcome = "consumer-stopped";

        // `messages` carries user content and system instructions — redact before shipping this
        // anywhere you would not put PII.
        logger.LogInformation(
            "LLM request: messages={Count}, tools={ToolCount}",
            messages.Count(),
            options?.Tools?.Count ?? 0);

        var stream = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                // `yield return` cannot live inside a try/catch, so the catch wraps only the
                // advance. This is what lets the outcome distinguish a fault from a cancellation
                // from a consumer that simply stopped reading.
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        outcome = "completed";
                        break;
                    }

                    update = stream.Current;
                }
                catch (OperationCanceledException)
                {
                    outcome = "canceled";
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = "faulted";
                    logger.LogError(ex,
                        "LLM call failed after {DurationMs}ms", stopwatch.ElapsedMilliseconds);
                    throw;
                }

                updates.Add(update);
                yield return update;
            }
        }
        finally
        {
            await stream.DisposeAsync();

            // Only a completed stream yields a trustworthy aggregate. Usage and finish reason are
            // absent or partial in every other terminal state, so do not report them as a result.
            if (outcome == "completed")
            {
                var response = updates.ToChatResponse();
                logger.LogInformation(
                    "LLM response completed: duration={DurationMs}ms, input_tokens={Input}, " +
                    "output_tokens={Output}, total_tokens={Total}, finish_reason={FinishReason}",
                    stopwatch.ElapsedMilliseconds,
                    response.Usage?.InputTokenCount,
                    response.Usage?.OutputTokenCount,
                    response.Usage?.TotalTokenCount,
                    response.FinishReason);
            }
            else
            {
                logger.LogWarning(
                    "LLM call ended without completing: outcome={Outcome}, " +
                    "duration={DurationMs}ms, partial_updates={Count}",
                    outcome, stopwatch.ElapsedMilliseconds, updates.Count);
            }
        }
    }
}
```

The `finally` runs on **every** terminal path — success, provider fault, cancellation, and a consumer
that stops enumerating early. Emitting one unconditional "LLM response" event from it would report a
partial or empty aggregate as if it were a result, and would hide failures behind a success-shaped
log line. Use `finally` for duration and cleanup; record the outcome explicitly.

`ChatResponseUpdate` fragments aggregate into a single `ChatResponse` via
`ToChatResponse()`, which is where `Usage` and `FinishReason` become available — they are not on the
individual updates.

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
    agent.ChatClient = sp => new LoggingChatClient(
        sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient(),
        sp.GetRequiredService<ILogger<LoggingChatClient>>());
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

To wrap the whole agent run rather than the model call, set `ConfigureAgentPipeline`
(`Action<AIAgentBuilder>`). Per-agent, or `DefaultConfigureAgentPipeline` on the options for every
agent; the per-agent value wins when both are set.

```csharp
opts.DefaultConfigureAgentPipeline = pipeline =>
    pipeline.UseOpenTelemetry(agentTelemetrySource);
```

That exact line is live in `samples/MAF/MultiAgentRouting/Program.cs`.

Custom middleware must be a **transparent, non-disposable `DelegatingAIAgent`**. The pipeline is
dry-built once at startup in a validation scope — so a broken pipeline fails fast rather than at
first request — and built once per activity attempt in that attempt's DI scope. MAF's built-in
`OpenTelemetryAgent` is owned and disposed by the per-build lease; do not dispose it yourself.

Middleware receives the exact restored `TemporalAgentSession` for the run. It may make retry-safe
StateBag changes, but it cannot replace the session.

---

## Your code runs inside an activity, not the workflow

The factory and everything it builds execute inside `RunDurableAgentStep`, once **per activity
attempt**. Four consequences, all of which bite in production:

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
2. Resolves context providers from that same scope. (Tools resolve once per worker from the root
   provider, not per attempt.)
3. Builds a `ChatClientAgent` with `UseProvidedChatClientAsIs = true`, so MAF does **not** auto-inject
   `FunctionInvokingChatClient` — the workflow owns tool dispatch.
4. Applies your `ConfigureAgentPipeline` middleware and the library's session boundary around it.
5. Calls `agent.RunStreamingAsync(...)` with one freshly built `ChatOptions` for the step.
6. Disposes the pipeline at the end of the attempt.

Three loops drive this, and your decorator fires under all of them: `AgentWorkflow` (sessions),
`TemporalAIAgent.RunTurnAsync` (sub-agents inside your own workflow), and `AgentJobWorkflow`
(scheduled runs).

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
| Decorator is constructed, but logs nothing | Logging lives in `GetResponseAsync`. The durable path only calls `GetStreamingResponseAsync`. |
| Streaming logs the request but never the completion | Completion logging sits after the `await foreach` instead of in a `finally`; an early break or a fault skips it. |
| `model=` is always empty | `options.ModelId` is not set by the library — the model is pinned on the provider client. |
| Usage and finish reason are always null | Read from `updates.ToChatResponse()`, not from individual `ChatResponseUpdate` values. |
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
