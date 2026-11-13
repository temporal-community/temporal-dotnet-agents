# Agent memory

The MAF integration distinguishes workflow-owned conversation state from application-owned
knowledge. Temporal history is not a searchable memory store, and retrieved memories are not a
source of truth for permissions, orders, or balances.

| Kind | What it holds | Where it lives |
|---|---|---|
| **Working** | Current conversation and per-session context state | Workflow history and the session `StateBag` |
| **Episodic** | Earlier conversations recalled by similarity | A vector store accessed through MAF's `ChatHistoryMemoryProvider` |
| **Semantic** | Facts, preferences, and relationships | An application-owned memory service or database |
| **Procedural** | Agent behavior | Instructions and registered tools |

## Working memory

The workflow owns each session's conversation history. Custom `ChatHistoryProvider` and
service-managed conversation history are not supported. The workflow carries history and `StateBag`
state across Continue-As-New; use the configured context reducer and model-context limits to bound
what is sent to the model.

## Episodic memory

MAF's `ChatHistoryMemoryProvider` stores conversation messages in a vector store and can recall
similar messages. The generic `AddContextProvider` path intentionally rejects this provider because
it cannot enforce the Temporal memory lifecycle.

```csharp
agents.Agent(
    "SupportAgent",
    new ChatClientAgentOptions { ChatOptions = new ChatOptions { Instructions = "..." } },
    temporal => temporal.UseChatHistoryMemory(memory =>
        memory.UseVectorStore(
            services => services.GetRequiredService<VectorStore>(),
            collectionName: "support-memory",
            vectorDimensions: 1536)));
```

The exact public signatures are:

```csharp
TemporalAgentOptions UseChatHistoryMemory(Action<TemporalChatHistoryMemoryBuilder> configure);

TemporalChatHistoryMemoryBuilder UseVectorStore(
    Func<IServiceProvider, VectorStore> factory,
    string collectionName,
    int vectorDimensions);

TemporalChatHistoryMemoryBuilder WithScopeInitializer(
    Func<IServiceProvider, TemporalMemoryScopeContext, CancellationToken,
        Task<ChatHistoryMemoryProvider.State>> initializer,
    bool allowCrossSessionRecall = false);

TemporalChatHistoryMemoryBuilder WithRecall(
    Action<ChatHistoryMemoryProviderOptions> configure);
```

The per-agent Temporal configuration callback receives a fresh `TemporalAgentOptions`; memory is
configured through that object. `TemporalMemoryScopeContext` is a sealed type exposing only
`SessionId`, `AgentName`, and `CollectionName`. For example, an optional trusted scope initializer
and recall configuration can be added inside the same callback:

```csharp
temporal => temporal.UseChatHistoryMemory(memory =>
{
    memory.UseVectorStore(
        services => services.GetRequiredService<VectorStore>(),
        collectionName: "support-memory",
        vectorDimensions: 1536);
    memory.WithScopeInitializer(ResolveAuthorizedScopeAsync); // optional
    memory.WithRecall(options => options.MaxResults = 5);      // optional
});
```

`WithScopeInitializer` is prominent for a reason: it establishes the trusted storage and search
scopes for the session. It runs in its own activity, before the first model call, only if the
workflow has no recorded memory state. The activity has a 30-second start-to-close timeout, at
most two attempts, a one-second initial retry interval, and no heartbeat timeout; it is independent
of worker model activity settings. A failed initialization surfaces as
`DurableTurnFailedException` before model invocation or the managed provider's search/storage
hooks, records no workflow memory state, and can be attempted again on the next turn. The
initializer itself may perform application-defined I/O. Invalid scope results are non-retryable.
The initializer can execute more than once if an activity attempt fails before its result is
recorded, so make it repeatable and safe to retry.

The initializer is optional. Without it, both storage and search use the current agent and Temporal
session as scope. This is the recommended default for session-only recall. If a vector collection
is shared across applications or Temporal namespaces, provide an initializer that adds a trusted
`ApplicationId` and/or `UserId` boundary. Derive those values from authenticated application state
and authorize the session request before creating or joining its workflow. `TemporalMemoryScopeContext`
provides the workflow session ID, registered agent name, and configured collection name; a
caller-supplied session ID is not proof of identity.

Cross-session recall is disabled by default: a search scope with a null session ID or a different
non-null session ID is rejected. To enable it, pass
`allowCrossSessionRecall: true` to `WithScopeInitializer` and return a search scope bounded by a
trusted application or user dimension. The storage scope remains bound to the current session.
Scope is validated before provider reads and writes on every activity attempt, including retries.
Authorization is established at the application session-request boundary; the integration does
not recheck authorization revocation during an in-flight workflow session. `WithRecall` configures
MAF options such as `SearchTime` (`BeforeAIInvoke` or `OnDemandFunctionCalling`) and `MaxResults`;
the library owns the provider state key, so configuring a different `StateKey` is rejected. Only
`OnDemandFunctionCalling` registers a memory-search tool under MAF's configured function name;
automatic `BeforeAIInvoke` recall does not reserve a tool name or collide with an application tool.
`BeforeAIInvoke` is MAF's default and the sample's default: it searches automatically before model
calls.
`OnDemandFunctionCalling` instead exposes the configured search function (`Search` by default),
whose call is dispatched as a Temporal tool activity using the initialized search scope. The
runnable sample demonstrates both; pass `--on-demand` to select function-calling recall.

Initialized provider state and its scope are stored in the workflow-owned session `StateBag`, then
carried through retries, Continue-As-New, and replay. If the scope marker exists but provider state
is missing, the workflow fails rather than silently re-running initialization. External vector
storage is not transactional with Temporal. MAF assigns a fresh record key when storing messages,
so a retry after a write succeeds but before activity completion is recorded can create duplicates.
Configure storage and retention to tolerate duplicate writes; the integration cannot make external
writes exactly once.

On-demand recall is a separate tool activity. Worker replacement can cause Temporal to reconstruct
the stock search function from the retained provider state and scope on a new worker. If the search
completes but the activity completion is not recorded, an explicitly configured agent tool retry
can repeat the external read. Tool retries are opt-in and apply to every tool on that agent; enable
them only when all tools are safe to repeat or use downstream idempotency.

See the runnable [ChatHistoryMemory sample](../../../samples/MAF/ChatHistoryMemory/), which uses
an in-memory vector store and deterministic demonstration embeddings. The in-memory store is
process-local and is not suitable for production persistence.

Changing the embedding model, vector dimensions, or collection may require a data migration or
reindex; Worker Versioning does not perform that work. Temporal Worker Versioning can route workflow
tasks to compatible worker deployment versions during a rollout, but it does not guarantee that an
external store is compatible or move data between store schemas. Plan and verify data migration
separately, and choose a rollout/rollback plan that keeps each workflow version paired with a
compatible store. See [Temporal Worker Versioning](https://docs.temporal.io/production-deployment/worker-deployments/worker-versioning).

## Semantic memory

Implement semantic recall with an application-owned `AIContextProvider`, such as an adapter over a
memory service or database, and register it with the agent. In managed sessions,
`InvokingAsync` prepares context once per logical turn in an activity; the recorded result is reused
for the model steps. Activity retries can repeat preparation.

The completion activity calls `InvokedAsync` for successful and failed turns. MAF's default
`InvokedCoreAsync` skips `StoreAIContextAsync` when `InvokeException` is set, so the default save
hook does not run for a failed turn. A custom `InvokedCoreAsync` can choose different behavior and
then owns its own filtering and failure handling. Completion activities may retry, so external
writes should be idempotent using an application-owned stable key.

## Procedural memory

Instructions and registered tools define behavior. Deploy changes to them like other workflow and
worker code; retrieved memories are context, not instructions or authorization.

## Rules of thumb

- Treat recalled memories as untrusted input; they may be stale, incorrect, or malicious.
- Recheck permissions against the system of record.
- Keep sensitive data out of memory stores unless your retention and deletion policies cover it.
