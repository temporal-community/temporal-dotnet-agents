# Bounded Durable `ChatClientAgent` Compatibility

`TemporalCommunity.Extensions.Agents` is a durable `ChatClientAgent` profile. It is not a general adapter for arbitrary Microsoft Agent Framework agent types or provider behavior.

The extension builds a fresh `ChatClientAgent` inside each LLM-step activity from the registered `IChatClient`, with `UseProvidedChatClientAsIs = true`. Temporal owns the model-step, durable-tool, retry, and approval boundaries.

## Supported inputs

| Input | Support | Required contract |
| --- | --- | --- |
| `IChatClient` registered through `DurableAgentBuilder.ChatClient` | Supported | The extension constructs the `ChatClientAgent` itself. |
| Instructions/messages-only `AIContextProvider` | Supported | It runs once per LLM step. Keep session state in `AgentSession.StateBag`; make external effects safe to retry. |
| Static provider tools declared through `IDurableToolSource` | Supported | The declarations are registered as Temporal tool activities. |
| Static tools supplied through `AddContextProvider(provider, durableTools)` | Supported | Use this adapter path when the provider type cannot implement `IDurableToolSource`. |
| Transparent `DelegatingAIAgent` middleware via `ConfigureAgentPipeline` | Supported with limits | It must pass the supplied `inner` agent to `base(inner)` so the exact library-created `ChatClientAgent` remains reachable. Its fields are activity-attempt-local, not session-local, and the wrapper must not implement `IDisposable` or `IAsyncDisposable`. |
| `OpenTelemetryAgent` / `OpenTelemetryChatClient` | Supported | The extension detects them to avoid emitting duplicate agent-turn telemetry. |

## Excluded inputs

| Input | Status | Why |
| --- | --- | --- |
| Arbitrary caller-built `AIAgent` | Not accepted | Durable registration takes an `IChatClient`; `A2AAgent`, graph agents, and their session protocols remain outside this contract. |
| Middleware factory that returns an unrelated agent | Rejected | It removes the library-created `ChatClientAgent`, so the configured provider and durable model/tool boundary are no longer guaranteed. |
| Opaque `AIAgent` wrapper | Rejected | A non-`DelegatingAIAgent` wrapper is not structurally inspectable, even if it privately stores and forwards to the supplied agent. |
| Custom disposable `DelegatingAIAgent` wrapper | Rejected | MAF 1.17.0 exposes no factory/DI ownership metadata. Put resource-owning dependencies in the activity scope; the library only owns the known `OpenTelemetryAgent` wrapper. |
| Function-invocation middleware or `FunctionInvokingChatClient` | Rejected | Inline function invocation bypasses durable tool activities, retries, approval, and Temporal visibility. |
| Tools dynamically returned by `AIContextProvider.InvokingAsync` | Disabled | The activity drops them and logs an error. Convert them into static durable declarations. |
| Provider-owned history or external writes (for example `ChatHistoryMemoryProvider`) | Unsupported | `InvokedAsync` runs in a retryable activity, and no atomic idempotent provider-history contract exists. |
| Providers that retain session data in process fields | Unsupported | Activity attempts can retry, move workers, and overlap sessions. Persist session state through `StateBag`. |
| `HarnessAgent` and a full Harness profile | Deferred | A Temporal-native Harness profile is a future research track, not a compatibility promise for `HarnessAgent`. |

## Provider lifetime and StateBag

Tool factories run once when the worker builds its immutable agent blueprint. Chat-client, context-provider, and interceptor factories run from a fresh DI scope for each activity attempt. An instance passed directly to `AddContextProvider` remains the caller's instance, but it still is not a session object.

For every LLM step, the activity restores `TemporalAgentSession` from the serialized `StateBag`, invokes registered providers, then returns the updated serialized bag to the workflow. This preserves supported provider state across tool-loop iterations, turns, worker restarts, and continue-as-new without relying on process-local fields.

## Agent middleware topology

`ConfigureAgentPipeline` decorates a `ChatClientAgent` created by this library for the current
activity attempt. Every custom wrapper must derive from `DelegatingAIAgent` and pass the factory's
exact `inner` argument to `base(inner)`. The library validates reference identity through the
resulting delegating chain at startup and again for live activity builds.

Startup validation uses a fresh DI scope. Every activity attempt, including a retry, constructs a
new live chain from that attempt's scope and disposes MAF's built-in `OpenTelemetryAgent` before
disposing the scope. No middleware object or mutable field is carried between attempts.

This is a structural requirement, independent of request behavior. A supported middleware may
short-circuit an individual run, recover from an exception, or replace messages/options while the
chain still retains its inner leaf. A factory that ignores `inner`, and an opaque `AIAgent`
subclass that manually forwards calls, are both rejected because neither exposes a verifiable
delegating path to the durable leaf.

## Consequence

For a third-party provider, support is determined by behavior—not by its package name. It is directly compatible only when it contributes retry-safe instructions/messages and stores session state in the supplied `StateBag`. If it produces tools, declare static `DurableToolRegistrationSpec` entries. If it owns history, invokes tools itself, or depends on live process state, do not register it until a dedicated durable adapter exists.
