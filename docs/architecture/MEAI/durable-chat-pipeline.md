# Durable chat pipeline architecture

`TemporalCommunity.Extensions.AI` maps a conversation ID to one long-lived Temporal workflow.
`DurableChatSessionClient.SendAsync` delivers each turn through a workflow update, so the
conversation state and completed activity results survive worker restarts.

## Managed tool loop

```
caller
  -> DurableChatSessionClient.SendAsync
  -> durable transport preparation
  -> DurableChatWorkflow [workflow update]
  -> GetChatStep [LLM activity]
     -> apply per-call activity tags [Temporal metadata visible]
     -> provider boundary [Temporal metadata removed]
     -> IChatClient provider
  -> InvokeFunction x N [tool activities]
  -> GetChatStep [next LLM activity]
  -> final ChatResponse
```

`GetChatStep` receives workflow-frozen declaration snapshots and passes them to the configured
`IChatClient`. Caller-owned mode supplies those snapshots at workflow start. Worker-owned mode
resolves registered toolsets into a versioned manifest before the first model step. The model
activity never discovers schemas from the live worker registry. When the model returns
`FunctionCallContent`, the workflow starts
one `InvokeFunction` activity per call and feeds the resulting `FunctionResultContent` messages
back to the next model step. Tool activities have their own timeout and retry options.

This is the only managed-session function loop. The public session API rejects
`ChatOptions.Tools`; the recorded declaration/manifest authority and matching worker registration
jointly define durable dispatch. A chat pipeline used by a durable session must not include
`UseFunctionInvocation()`, which would execute functions outside the workflow-owned activity loop.

## Components

| Component | Responsibility |
| --- | --- |
| `DurableChatSessionClient` | Atomically starts/reuses the workflow and sends the chat update. |
| `DurableChatWorkflow` | Stores session history and drives model/tool iterations. |
| `DurableChatActivities.GetChatStepAsync` | Resolves the `IChatClient`, receives frozen declarations, and makes one model call. |
| `DurableToolsetActivities.ResolveDurableToolsetsAsync` | Resolves worker-owned toolsets to a versioned, implementation-free manifest. |
| `DurableFunctionActivities.InvokeFunctionAsync` | Resolves and invokes one registered function as an activity. |
| `DurableFunctionRegistry` | Worker-local, startup-built map of stable function names to `AIFunction` implementations. |
| `DurableAIDataConverter` | Preserves MEAI polymorphic content such as function call and result messages in Temporal payloads. |
| `ChatOptionsSanitizer` | Separates durable-transport preparation from provider-boundary removal of Temporal-private keys. |

## Chat options boundaries

Durable transport begins with `ChatOptions.Clone()`. It retains serializable Temporal settings,
client routing, tag entries, ordinary MEAI options, and user-owned additional properties.
It removes only `RawRepresentationFactory` and `ContinuationToken`, which cannot be safely resumed
across the activity boundary.

After the activity resolves the provider, it applies configured activity tags to
`Activity.Current`, then invokes the provider through a boundary client. The boundary clones the
invocation options and removes every Temporal-owned key immediately before the provider call. Both
`GetResponse` and `GetChatStep` use the same boundary. The activity validates the resolved client
chain and rejects `FunctionInvokingChatClient` when durable tools are registered, preserving the
workflow-owned tool loop. Other model-call concerns use ordinary MEAI `IChatClient` composition.

The default converter preserves arbitrary user properties by JSON content. Because
`AdditionalProperties` values are object-typed, they may deserialize as `JsonElement`; original CLR
runtime types are not promised. Library-owned getters normalize this shape for client keys,
tags, activity and heartbeat timeouts, and maximum retry attempts.

## Direct-adapter task-queue boundary

The public `DurableChatClient` and `DurableEmbeddingGenerator` adapters can be constructed inside a
custom workflow. Their `DurableExecutionOptions.TaskQueue` is assigned directly to the Temporal
`ActivityOptions.TaskQueue` for each provider call:

```
custom workflow worker [workflow task queue]
  -> DurableChatClient / DurableEmbeddingGenerator
  -> Temporal activity [DurableExecutionOptions.TaskQueue]
  -> worker-side IChatClient / IEmbeddingGenerator
```

The configured queue does not change the workflow's own task queue. This makes workflow and AI
activity workers independently deployable. If the adapter omitted the activity queue, the Temporal
SDK would default the activity to the workflow queue; the adapters set it explicitly.

## Boundary and deployment rules

- The converter belongs to a client or worker, not an individual workflow. A worker configured by
  `AddDurableAI()` writes payloads for every workflow it serves, including ordinary application
  workflows; all clients that exchange those payloads need a compatible converter. See
  [the usage guide](../../how-to/MEAI/usage.md#sharing-a-worker-with-non-ai-workflows).
- Every worker on the task queue must register the same durable tool names and compatible schemas.
- Direct-adapter activity workers must poll the queue configured on
  `DurableExecutionOptions.TaskQueue`; workflow workers may poll a different queue.
- `AsDurable()` is a separate direct-function primitive for custom workflows. It does not alter the
  managed-session contract. Its function activity leaves `ActivityOptions.TaskQueue` unset and
  therefore runs on the calling workflow's task queue; `DurableExecutionOptions.TaskQueue` does
  not reroute this adapter.
