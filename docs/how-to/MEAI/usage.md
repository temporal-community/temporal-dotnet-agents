# Using TemporalCommunity.Extensions.AI

`TemporalCommunity.Extensions.AI` makes a MEAI `IChatClient` durable with Temporal. Choose the
integration that matches who should own conversation state and workflow code:

- **Managed sessions:** use `DurableChatSessionClient` for a multi-turn conversation with
  package-managed history, tool dispatch, and continue-as-new; see
  [managed sessions](#built-in-managed-session).
- **Custom workflows with session features:** derive from
  `DurableChatWorkflowBase<TOutput>` or `DurableToolWorkflowBase<TRequestData, TTurnState>` when
  your workflow needs custom orchestration but should retain the package's session lifecycle; see
  [custom workflow output](custom-workflow-output.md).
- **Custom workflows without session features:** call `IChatClient` from your own activity, and
  use `AIFunction.AsDurable()` when an individual tool call also needs activity durability; see
  [Durable tool calls](#durable-tool-calls-with-aifunctionasdurable).

## Built-in managed session

For recurring turns, see [scheduled durable chat](scheduling.md): use a native Temporal Schedule
to start a trigger workflow whose activity submits the turn, rather than scheduling a session
workflow directly.

For capacity isolation, [the operating guide](operations.md) shows optional `ModelTaskQueue`
and per-tool `TaskQueue` routing. Defaults remain on the workflow queue; routing is frozen for
the session, not chosen per turn.

Register a `ChatClient`, a durable tool, and the worker in one place. This quick start shows
activity and model limits, Temporal history budgets, and a per-tool override:

```csharp
builder.Services.AddTemporalClient("localhost:7233", "default");
builder.Services.AddChatClient(innerChatClient);

var weatherTool = AIFunctionFactory.Create(
    (string city) => weather.GetCurrent(city),
    name: "get_weather",
    description: "Gets current weather for a city.");

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options =>
    {
        options.ActivityTimeout = TimeSpan.FromMinutes(5);
        options.SessionTimeToLive = TimeSpan.FromHours(24);
        options.MaxToolCallsPerTurn = 10;
        options.MaxModelContextTokens = 8192;
        options.ModelOutputTokenReserve = 1024;
        options.MaximumTransportPayloadSizeBytes = 1024 * 1024;
        options.PayloadCodecExpansionAllowanceBytes = 0;
        options.HistoryEventSoftLimit = 40_000;
        options.HistoryEventReserveLimit = 45_000;
        options.HistorySizeSoftLimitBytes = 40 * 1024 * 1024;
        options.HistorySizeReserveLimitBytes = 45 * 1024 * 1024;
        options.MaxConcurrentTurnJoiners = 32;
    })
    .AddDurableTool(weatherTool, tool => tool.WithTimeout(TimeSpan.FromSeconds(30)));
```

The optional model-context limit is independent of Temporal payload limits. The default
deterministic heuristic estimates messages, instructions, tools and response schema; register an
`IDurableChatTokenEstimator` in worker DI to replace it with a provider/model-specific estimator.
The built-in heuristic caches message byte estimates within each fitting operation rather than
reserializing each candidate suffix. Custom estimators still receive each complete candidate
in order; their estimates need not be additive or monotonic.
The output reserve is included in `MaxModelContextTokens`. When over budget, the activity removes
only complete, eligible prior turns, preserving the active turn and protected approval or
interrupted history. If the protected request still cannot fit, it fails with
`DurableContextBudgetExceededException` rather than silently dropping content. In split hosting,
configure matching limits for client and worker so the workflow input and activity policy agree.

The durable AI registration configures DI-created clients from `AddTemporalClient(...)` to use
`DurableAIDataConverter.Instance` when the converter is still the SDK default. `AddTemporalClient`
alone does not install it; manually connected clients (`TemporalClient.ConnectAsync`) must be
configured with a compatible converter. `AddDurableAI()` validates the registered client before the
worker starts; see
[worker setup](#worker-setup-with-a-manual-client) below if you connect manually.

- ❌ Don't call `UseFunctionInvocation()` on the `IChatClient` used by a managed session.
- ❌ Don't pass `ChatOptions.Tools` to `SendAsync`.

The workflow owns tool selection and dispatch, and both are rejected. For the full
tool-registration surface (`AddDurableTools`, `AddDurableToolset`, `AddDurableToolFactory`) see
[tool functions](tool-functions.md) and the
[managed-session tool rules](managed-session-tool-rules.md).

### Payload and activity-retry bounds

The default 1 MiB transport budget is a conservative per-payload ceiling, not a model-context or
aggregate event-history budget. The guard measures the payload converter's complete Temporal
payload, including metadata, and subtracts `PayloadCodecExpansionAllowanceBytes` before comparing
it with `MaximumTransportPayloadSizeBytes`. Lower the maximum for deployments with stricter
service limits; only raise it when the configured Temporal service allows it. The limit covers
workflow input, update input/result, query result, Continue-as-New input, and managed/direct
activity inputs and outputs. Model outputs that exceed the budget fail as a bounded non-retryable
activity failure. When a tool effect succeeds but its output exceeds the budget, the activity
returns a bounded `ResultTooLarge` result to the model and does not rerun the tool. Activity
failure details are bounded before transport; retryability and recognized failure types are
preserved, but original error text and stack details are not transported.

The built-in gzip codec never expands a payload, so it needs no allowance. A custom codec must
declare a known maximum expansion in `PayloadCodecExpansionAllowanceBytes`; the workflow can access
the configured payload converter but not run the codec itself. A custom codec without a known
expansion bound cannot provide this guard's conservative guarantee. A passing guard also does not
prove that a later codec, aggregate command, or deployment-specific request envelope fits.

Unconfigured activity policies use the library's finite five-attempt default. An explicit
`RetryPolicy` must specify positive `MaximumAttempts`; `MaximumAttempts = 0` delegates to Temporal's
unlimited server default and is rejected. `MaxToolCallsPerTurn` separately bounds the model/tool
loop.

### Worker setup with a manual client

If you connect the Temporal client manually instead of using `AddTemporalClient`, set the durable
converter explicitly:

```csharp
var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
});

builder.Services.AddSingleton<ITemporalClient>(client);
builder.Services.AddChatClient(innerChatClient);

builder.Services
    .AddHostedTemporalWorker("durable-chat")
    .AddDurableAI(options => options.SessionTimeToLive = TimeSpan.FromHours(24));
```

`DurableAIDataConverter.Instance` remains uncompressed. For large histories, an optional bounded
gzip codec is available — see [payload codecs](payload-codecs.md).

### Split client and worker hosting

For separate deployments, register only session-starting services in the client process and only
workflow/activity services in the worker process. The client process does not need the provider's
`IChatClient`, model credentials, or tool implementations; those belong on the worker:

```csharp
// Client process
clientBuilder.Services.AddTemporalClient("localhost:7233", "default");
clientBuilder.Services.AddDurableAIClient("durable-chat", options =>
{
    options.SessionTimeToLive = TimeSpan.FromHours(24);
    options.MaxToolCallsPerTurn = 10;
});

// Worker process
workerBuilder.Services.AddChatClient(innerChatClient);
workerBuilder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAIWorker()
    .AddDurableTool(weatherTool);
```

`AddDurableAIWorker()` does not require `ITemporalClient` in worker DI; the three-argument
`AddHostedTemporalWorker` overload owns the worker connection. `AddDurableAIClient()` does require
a DI `ITemporalClient`, and auto-configures the durable converter when it is created with
`AddTemporalClient`. Manually constructed clients must set
`DataConverter = DurableAIDataConverter.Instance` explicitly. The client and worker must use the
same task queue. The client-only registration can be added before or after `AddTemporalClient`; a
missing client is reported when `DurableChatSessionClient` is resolved.

The client freezes session policies such as lifetime, iteration caps, context and transport
budgets, event-history thresholds, and the concurrent joiner limit into the workflow start input.
Configure those on `AddDurableAIClient`; worker-only settings cannot override an already-started
session's frozen input. Provider implementations, credentials, and activity-local services remain
worker-owned.

`AddDurableAIClient` validates the client process's task queue and validates its converter when
`DurableChatSessionClient` is resolved; it cannot inspect a remote worker's provider registrations
or toolsets. Worker startup validates the configured default chat-client profile and worker-owned
default toolsets without constructing the provider client. A managed session pins the profile
configured through `DefaultChatClientKey` when it starts; a per-turn `WithChatClientKey(...)`
override must match that profile. The workflow input records only the profile key and serializable
declarations/policies, never provider instances, credentials, or executable tool services.
The recorded tool declarations and policies do not freeze the implementation registered behind a
stable worker-side activation key; deployments must preserve the behavior expected by recorded
declarations.

For a co-hosted deployment, `AddDurableAI()` remains the concise combined registration shown in
[Built-in managed session](#built-in-managed-session).

### Session lifetime and lineage

`SessionTimeToLive` is an idle lifetime, not a per-run timer. The workflow stores an absolute
`Workflow.UtcNow` idle deadline, renews it when a turn is admitted or completes, and carries it
unchanged through Continue-as-New. An active turn—including a turn waiting for tool approval—is
drained rather than expired; completion renews the deadline before the workflow resumes its idle
wait. When the session expires, the workflow closes and a later send may start a new lineage using
the same workflow ID.

Read the current lineage with `GetSessionEpochAsync` and pass it as `expectedSessionEpoch` when a
request must not silently run in a restarted session:

```csharp
var epoch = await sessionClient.GetSessionEpochAsync("customer-42");
var response = await sessionClient.SendAsync(
    "customer-42",
    "Continue the same conversation.",
    requestId: "turn-17",
    expectedSessionEpoch: epoch);
Console.WriteLine(response.Text);
```

An epoch mismatch throws `DurableChatSessionEpochMismatchException` before turn admission or
provider/tool effects. Callers without a previously stored epoch cannot distinguish a first
session from a new lineage after expiry; persist the epoch alongside the application session
identity when that distinction matters.

The client uses `WorkflowIdConflictPolicy.UseExisting` for an active session and explicitly allows
workflow-ID reuse after a closed run. Reuse starts a new epoch rather than restoring expired
history. Use the expected-epoch precondition to detect that restart.

### Sharing a worker with non-AI workflows

Temporal configures a data converter per client or worker, not per workflow. When the registered
client still uses the SDK default converter, `AddDurableAI()` configures
`DurableAIDataConverter.Instance` for every workflow and activity served by that worker's client,
including ordinary application workflows registered on the same worker. A custom converter is
preserved and must be compatible with every payload exchanged through that client.

Every client that starts, signals, queries, or reads results from those workflows must use a
compatible converter. The durable AI registration configures a DI-created client registered with
`AddTemporalClient(...)` only when its converter is still the SDK default; `AddTemporalClient(...)`
alone does not install the converter. A manually connected client bypasses that DI configuration:

```csharp
var options = ClientEnvConfig.LoadClientConnectOptions();
options.DataConverter = DurableAIDataConverter.Instance;
var client = await TemporalClient.ConnectAsync(options);
```

An incompatible client can deserialize a payload into an otherwise valid application object whose
nested members are null or default values; that mismatch need not throw at deserialization time.
The Temporal service does not retain converter identity, so this library cannot diagnose a converter
chosen independently by another process. A custom converter is preserved by the registration APIs,
but it must remain compatible with the MEAI and application payloads used by every participating
client and worker.

### Keyed client profiles

For a keyed client, register it with `AddKeyedChatClient` and set
`DurableExecutionOptions.DefaultChatClientKey` on both the session client and worker. The key is
captured in the workflow input at session creation and carried through Continue-as-New, so a
session stays on one profile. A `WithChatClientKey(...)` option on a managed session is accepted
only when it matches the captured profile; choose a different profile by starting a different
session with a new conversation ID and the corresponding client configuration. The worker resolves
the keyed service from its DI container inside the model activity, not from workflow history.

The worker checks that the configured default profile is registered at startup without invoking
the provider factory. The profile key is frozen, but the client implementation and credentials
behind that key remain worker-owned runtime services and are resolved for each model activity:

```csharp
clientBuilder.Services.AddTemporalClient("localhost:7233", "default");
clientBuilder.Services.AddDurableAIClient(
    "durable-chat",
    options => options.DefaultChatClientKey = "primary");

workerBuilder.Services.AddKeyedSingleton<IChatClient>("primary", innerChatClient);
workerBuilder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAIWorker(options => options.DefaultChatClientKey = "primary");
```

### Context-only chat reducers

Set `DefaultContextReducerKey` in the session-client configuration; the value is captured in each
new workflow input and survives Continue-as-New. Register an `IChatReducer` under the same key in
worker DI. It receives deep copies of eligible complete prior turns and can shape the ephemeral request without
changing `GetHistory` or retained session entries. System/developer instructions, the active turn,
approvals, and interrupted turns are protected. Reducer output is checked for complete tool-call/
result exchanges and rechecked against the configured model-context budget; malformed or
still-oversized output fails before the provider call instead of being silently trimmed.

```csharp
clientBuilder.Services.AddTemporalClient("localhost:7233", "default");
clientBuilder.Services.AddDurableAIClient(
    "durable-chat",
    options => options.DefaultContextReducerKey = "recent-context");

#pragma warning disable MEAI001
workerBuilder.Services.AddKeyedSingleton<IChatReducer>(
    "recent-context",
    new MessageCountingChatReducer(20));
#pragma warning restore MEAI001

workerBuilder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAIWorker();
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/MEAI/ContextReducer.cs. -->

The reducer is activity work, not workflow code, but an activity retry may invoke it again.
Configure an implementation that is deterministic for the same input and safe for concurrent calls;
provider calls made by a reducer have their own retry and cost implications. MEAI's built-in
`MessageCountingChatReducer` can omit entire messages containing tool calls or results, while its
`SummarizingChatReducer` returns an unmarked summary. The library validates grammar but does not
interpret a reducer's omissions as retained-history compaction.

### Library-owned history summarizer

The history-summarization activity uses a worker-registered `IChatClient`; it does not call a
network-backed client from workflow code. Configure an optional keyed summarizer profile and a
separate context/output budget. If no profile key is set, the session's frozen default chat-client
profile is used:

```csharp
var summaryOptions = new DurableExecutionOptions
{
    TaskQueue = "durable-chat",
    DefaultChatClientKey = "primary",
    DefaultHistorySummarizerClientKey = "summarizer",
    HistorySummarizerMaxContextTokens = 8192,
    HistorySummarizerOutputTokenReserve = 1024,
    DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.SummarizeOldestTurns,
};
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/MEAI/HistorySummarizer.cs. -->

The activity selects the largest oldest prefix of complete turns that fits this budget, counting
its fixed instructions and output reserve. Tool calls and results are serialized into the prompt as
historical data; no tool is dispatched. Content types the library cannot render safely fail
explicitly rather than disappearing from the summary input. Protected turns and turns containing
system/developer instructions or approval content are rejected. A single turn that cannot fit
blocks summarization. Token counts use the configured `IDurableChatTokenEstimator` or its
deterministic heuristic default; estimates are not a provider-enforced limit. The resulting summary
includes model and usage metadata. Missing client-profile configuration is a non-retryable activity
failure (and is checked at startup for the stock workflow). Provider calls may be retried according
to the activity policy, so retries can incur additional cost. Summary text can contain sensitive
conversation data and, when later committed by the compaction strategy, remains part of Temporal
workflow history; compaction does not erase prior events.

The strategy is applied only at a safe Continue-as-New boundary when the entry-count trigger fires.
The library validates the projection, stores a bounded compaction marker in retained history, and
projects that summary into later model requests. When transport payload guards are enabled, they
cover the summarizer activity input/result, the compacted history including its marker, and the
complete Continue-as-New input. An oversized payload blocks compaction without replacing retained
history; inspect the history-recovery status and adjust the payload or codec budget deliberately.
These per-payload checks cannot prove that the codec's actual encoded bytes, aggregate Temporal
request envelopes, or service/deployment limits will fit. Prior Temporal events are not erased.

### Event-history budgets and concurrent requests

The history values in the managed-session configuration example are the defaults: 40,000 events
and 40 MiB trigger soft admission restriction; 45,000 events and 45 MiB are reserved for active
turns to finish. At the soft threshold, new turns are rejected as `DurableHistoryBudgetBlocked`
and the workflow prepares to continue as new after admitted work drains. A matching duplicate of
the active request may still join, up to `MaxConcurrentTurnJoiners` (default 32); further matching
requests fail before acceptance with non-retryable `DurableTurnPending`. The query below reports
the current joined-caller count but does not identify individual pending requests.

At either reserve threshold, an unresolved approval expires as a denial with
`DurableApprovalExpirationReason.HistoryBudget`, allowing the active turn to finish. Event and
byte thresholds are checked independently; each soft limit must be positive and below its reserve.
In split hosting, configure these values on the client-side `AddDurableAIClient` registration
because they are frozen into the workflow start input.

`GetHistoryBudgetAsync` returns event count and size, their configured thresholds, admission and
reserve flags, joiner counts, retained-entry count, and history-recovery state. The values reflect
the latest workflow-task boundary and can be stale between tasks.

### Retained-history compaction and recovery

`GetHistoryAsync` returns the session's current retained history, not an archive. `MaxEntryCount`
defaults to 1000 entries and starts retained-history rollover handling. Unlike context-only
`IChatReducer`s, the retained-history strategy can replace old complete turns with a summary or a
truncation marker. No strategy is selected by default: if history reaches its count threshold
without a configured keyed history reducer or compaction strategy, the workflow preserves the last
valid history, closes new-turn admission, and enters queryable `HistoryBlocked` rather than
silently deleting entries.

Select one explicit strategy when registering the worker:

```csharp
var trimOptions = new DurableExecutionOptions
{
    TaskQueue = "durable-chat",
    DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns,
};
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/MEAI/HistorySummarizer.cs#retained-history-compaction-options. -->

`HistoryReducerKey`, when configured, takes precedence and remains the custom retained-history
projection path. `TrimRecentTurns` is intentionally lossy and records the removed range with a
truncation marker. `SummarizeOldestTurns` uses the worker-owned `IChatClient` activity described
above. Both protect the current turn and validate the resulting message/tool grammar and payload
limits before changing retained state. An existing summary marker is included only when another
eligible older turn can be summarized with it; the worker does not spend a summarization call
reprocessing a marker by itself when that cannot reduce retained history.

Inspect `GetHistoryBudgetAsync` for `IsHistoryBlocked`, `HistoryBlockReason`, retained entry count,
and recovery counters. While blocked, new turns are rejected and idle TTL does not discard the
session. A caller may use `RetryHistoryCompactionAsync` with a distinct persisted operation ID to
retry the configured reducer/strategy, at most three accepted retry operations per workflow run
and nine per incident across Continue-as-New. A single `TrimBlockedHistoryAsync` operation is
available per incident as an explicitly lossy fallback; it is not automatic. Duplicate,
concurrent, over-limit, or reserve-exhausted recovery requests are rejected by update validators
before acceptance. If history headroom or protected content prevents recovery, the session remains
blocked; Temporal/server command limits can still prevent progress.

Successful recovery closes new-turn admission and drains all already-admitted handlers and
background turns before restarting. A recovery result reports that its projection succeeded,
not that Continue-as-New has already happened. If an admitted turn finishes during the drain,
the projection's source snapshot is tracked across the reducer await, then rebuilt from the
completed history and validated again; a failure leaves the
session blocked rather than carrying a partial or stale turn. Pending approvals stay in the old
run until resolved, cancelled with their turn, or expired by their finite approval timeout or
history reserve. As with normal compaction, an elapsed idle deadline or `RequestShutdown` does
not abandon admitted handlers: shutdown waits for that drain and then completes without restarting.

During a normal Continue-as-New transition, new sends may fail before acceptance with
`DurableTurnContinueAsNewPending`; retry that explicit rejection with bounded backoff. A network
timeout is ambiguous and may follow an accepted turn. Already-admitted turns finish before the
transition. `ShutdownAsync` also stops admission and waits for admitted turns, including those
paused for approval.

```csharp
var status = await sessionClient.GetHistoryBudgetAsync(conversationId);
Console.WriteLine(
    $"History: {status.HistoryEventCount}/{status.HistoryEventSoftLimit} events " +
    $"({status.HistoryEventReserveLimit} reserve); " +
    $"{status.HistorySizeBytes}/{status.HistorySizeSoftLimitBytes} bytes " +
    $"({status.HistorySizeReserveLimitBytes} reserve).");
Console.WriteLine(
    $"Admission restricted: {status.IsAdmissionRestricted}; reserve reached: {status.IsReserveReached}; " +
    $"joiners: {status.ConcurrentTurnJoiners}/{status.MaxConcurrentTurnJoiners}.");
if (status.IsHistoryBlocked)
{
    var recovery = await sessionClient.RetryHistoryCompactionAsync(
        conversationId,
        operationId: Guid.NewGuid().ToString("N"));
    if (recovery.IsBlocked)
    {
        await sessionClient.TrimBlockedHistoryAsync(
            conversationId,
            operationId: Guid.NewGuid().ToString("N"));
    }
}
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/MEAI/HistorySummarizer.cs#retained-history-compaction-and-recovery. -->

These supported updates are covered by the library's event-history budget. Arbitrary direct
Temporal signals or updates outside this API are not; accepted raw signals are recorded even when
no workflow signal handler exists. Restrict direct workflow access and apply appropriate
application/deployment access controls.

## Send and manage turns

### Send a turn and read history

```csharp
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

// Persist this ID before submitting if the caller must retry an ambiguous RPC.
var requestId = Guid.NewGuid().ToString("N");
var response = await sessionClient.SendAsync(
    "customer-42",
    "What is the weather in Seattle?",
    requestId: requestId);

Console.WriteLine(response.Text);

var history = await sessionClient.GetHistoryAsync("customer-42");
```

### Request identity, admission, and retries

Persist a caller-supplied `requestId` before sending when recovery across a client crash matters.
`correlationId` is for observability, and the Temporal workflow-update ID is a separate transport
identity. If the client generated the request ID, `DurableChatRequestException.RequestId` exposes
it after a failed submission. The workflow fingerprints messages, model options, the pinned client
profile, and recorded tool authority. A matching ID and fingerprint joins an active turn; a
different ID while a turn is active is rejected as `Busy`, and changed input under the same ID is
rejected as `Conflict`.

The terminal-outcome ledger is bounded by `MaxTerminalOutcomeCount` (default 128) and
`MaxTerminalOutcomeBytes` (default 1 MiB); pending requests are not evicted. Eviction or an
oversized outcome ends retention for that request, so a later retry can execute again. While
admission is open, a matching retry returns a retained terminal outcome, including across
Continue-as-New. If admission is restricted by the history budget, the retry may be rejected before
the terminal ledger is checked; use `GetTerminalTurnOutcomeAsync(conversationId, requestId)` to
resolve an ambiguous send instead. That query returns retained terminal outcomes only; `null` is
not a per-request pending status and may also mean the outcome is no longer retained. This ledger
does not guarantee exactly-once external effects; make effectful tools idempotent and reconcile
uncertain outcomes with the system of record.

### Send schema-bound structured output

Use `SendStructuredAsync<T>` when the response must deserialize to a non-null object. It generates
the MEAI JSON schema from `T`, sends that schema as `ChatOptions.ResponseFormat`, and returns an
MEAI `ChatResponse<T>` with the parsed `Result` and response metadata:

```csharp
var requestId = Guid.NewGuid().ToString("N"); // Persist before submission if retries are possible.
var response = await sessionClient.SendStructuredAsync<WeatherSummary>(
    "customer-42",
    "Return a JSON object with the weather summary.",
    requestId: requestId);

Console.WriteLine(response.Result.Summary);
Console.WriteLine(response.ResponseId);
```

`WeatherSummary` must produce a JSON object at the schema root; wrap primitive and array results in
an object type. The provider must support JSON-schema structured output. Leave
`ChatOptions.ResponseFormat` unset because the requested type owns the schema. `requestId`
fingerprints the generated schema together with the messages and other request options, so retries
must use the same type and serializer settings. `DurableStructuredOutputException` is thrown
after a successful durable turn if the response is incomplete, malformed, or cannot deserialize as
a non-null `T`; its `Response` property retains the original durable response for inspection.

The returned `ChatResponse<T>` retains MEAI response ID, provider conversation ID, model ID,
finish reason, usage, and serializable additional properties. Provider `RawRepresentation` is an
in-memory SDK object and is not carried through Temporal; additional-property values are restored
as `JsonElement`. Use `serializerOptions` when a non-default JSON contract is required.

`SendAsync` accepts either one user string or an enumerable of MEAI `ChatMessage` instances.
The application conversation ID is the durable session identity; provider-owned
`ChatOptions.ConversationId` is rejected. `RawRepresentationFactory` and `ContinuationToken` are
also rejected because delegates and opaque provider continuation state cannot safely cross the
workflow/activity boundary.

The returned `DurableSessionResponse` preserves per-turn usage, finish and completion reasons,
provider response/model IDs, and serializable provider `AdditionalProperties`. The
`ResponseAdditionalProperties` values are represented as `JsonElement`, not as the provider's
original CLR types. For a managed tool loop, provider IDs and metadata come from its terminal model
step. `GetHistoryAsync` returns the currently retained
`IReadOnlyList<DurableSessionEntry>`. Tool
calls are recorded as `TemporalCommunity.Extensions.AI.InvokeFunction` activities. Streaming is not
supported for durable sessions — there is no `GetStreamingResponseAsync` equivalent on
`DurableChatSessionClient`. See [tool functions](tool-functions.md) for the complete tool
registration and dispatch contract, and [HITL patterns](hitl-patterns.md) for pausing a turn on
tool approval.

#### Cancel an active turn

Turn cancellation is a separate workflow update; cancelling the `SendAsync` RPC wait does not
cancel work already accepted by the workflow. Persist and reuse the same request ID to address the
turn:

```csharp
var requestId = Guid.NewGuid().ToString("N"); // Persist before submission.
var turn = sessionClient.SendAsync(
    "customer-42",
    [new ChatMessage(ChatRole.User, "Run the long operation.")],
    requestId: requestId);

var cancellation = await sessionClient.CancelTurnAsync("customer-42", requestId);
if (cancellation.Status == DurableTurnCancellationStatus.CancellationRequested)
{
    Console.WriteLine("The workflow requested cancellation.");
}
```

### Cancellation and interrupted turns

`CancelTurnAsync` targets only the matching active request. A cancellation race can return
`AlreadyCompleted`; a request that no longer matches is rejected before acceptance. Observe the
original send task for its terminal outcome. The update cancels active model, interceptor, or tool
activities and interrupts pending approval. Temporal waits for activity cancellation; activity code
and downstream operations must cooperate. Cancellation tokens passed to the RPCs cancel only those
RPC waits, and cancellation does not undo external effects.

If failure or cancellation occurs after a tool activity is dispatched, retained history records an
`Interrupted` response: confirmed results, `unknown` outcomes for dispatched calls whose effects
cannot be confirmed, and `not executed` for undispatched calls. Partial assistant text is omitted.
The record is capped at 64 KiB, preserving tool-call/result grammar with bounded placeholders when
needed. Before dispatching each tool batch, the workflow reserves space for all call identities and
bounded results; if the next batch cannot fit, it fails with
`DurableInterruptionRecordBudgetExceeded` before dispatching that batch. A turn that fails before
tool dispatch leaves no request/response pair. Reconcile uncertain effects before retrying them.

#### Inspect interrupted history

```csharp
try
{
    await turn;
}
catch
{
    var interrupted = (await sessionClient.GetHistoryAsync("customer-42"))
        .OfType<DurableSessionResponse>()
        .LastOrDefault(entry =>
            entry.RequestId == requestId
            && entry.CompletionReason == DurableTurnCompletionReason.Interrupted);
    if (interrupted is not null)
    {
        Console.WriteLine("The turn was interrupted after a tool dispatch; reconcile its effects.");
    }

    throw;
}
```

#### Recovering a generated request ID

When the library generated the ID, retain it from `DurableChatRequestException` if the submission fails:

```csharp
try
{
    await sessionClient.SendAsync("customer-42", "What is the weather in Seattle?");
}
catch (DurableChatRequestException exception)
{
    var requestId = exception.RequestId; // Persist it before retrying the same input.
    Console.WriteLine(requestId);
    throw;
}
```

### Retained-history identities

Each library-created request and response has its own stable `EntryId`; both share a `TurnId`.
The identifiers distinguish retained session entries from observability correlation IDs and are
preserved when the workflow continues as new.

```csharp
// Inspect the stable identities associated with retained request/response pairs.
var history = await sessionClient.GetHistoryAsync("customer-42");
foreach (var turn in history
    .OfType<DurableSessionRequest>()
    .Where(request => request.EntryId is not null && request.TurnId is not null))
{
    var response = history
        .OfType<DurableSessionResponse>()
        .FirstOrDefault(entry => entry.TurnId == turn.TurnId);
    Console.WriteLine($"{turn.TurnId}: {turn.EntryId} -> {response?.EntryId}");
}
```

<!-- The executable form of this example is compiled from tests/docs/DocSnippets/Snippets/MEAI/RetainedHistoryIdentity.cs. -->

## Custom workflows

### Custom typed-turn admission

Custom workflows using `DurableToolWorkflowBase` should pair the turn update with a synchronous
validator that calls `ValidateDurableTurnAdmission`. This ensures malformed, busy, and conflicting
requests are rejected before acceptance. The handler also performs admission defensively; update
validators must not await or depend on workflow input that may not yet be initialized.

```csharp
public sealed record ApplicationRequest(string OperationId);
public sealed record ApplicationTurnState(int Revision);

[Workflow("ApplicationWorkflow")]
public sealed class ApplicationWorkflow
    : DurableToolWorkflowBase<ApplicationRequest, ApplicationTurnState>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdateValidator(nameof(TurnAsync))]
    public void ValidateTurn(
        DurableTurnRequest<ApplicationRequest, ApplicationTurnState> request) =>
        ValidateDurableTurnAdmission(request);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ApplicationTurnState>> TurnAsync(
        DurableTurnRequest<ApplicationRequest, ApplicationTurnState> request) =>
        RunDurableTurnAsync(request);
}
```

See [custom workflow output](custom-workflow-output.md) and the runnable
[`ExtensibleDurableTurns` sample](../../../samples/MEAI/ExtensibleDurableTurns/README.md) for the
complete typed-turn contract.

### Per-request overrides with `ChatOptions`

`TemporalChatOptionsExtensions` writes per-request settings onto `ChatOptions.AdditionalProperties`.
Each extension mutates the options instance in place and returns it. For custom workflows using
`DurableChatClient`, a per-request timeout or retry value takes precedence over the matching
`DurableExecutionOptions` default. Managed sessions use their captured activity policies instead;
these timeout and retry extensions do not override those policies.

| Extension | Key constant | Overrides |
|---|---|---|
| `WithActivityTimeout(TimeSpan)` | `ActivityTimeoutKey` | `DurableExecutionOptions.ActivityTimeout` |
| `WithMaxRetryAttempts(int)` | `MaxRetryAttemptsKey` | Maximum attempts of the retry policy |
| `WithHeartbeatTimeout(TimeSpan)` | `HeartbeatTimeoutKey` | `DurableExecutionOptions.HeartbeatTimeout` |
| `WithChatClientKey(string)` | `ChatClientKeySettingKey` | Per-call keyed `IChatClient` for custom workflows; must match the captured managed-session profile |
| `WithChatClientTag(string, string)` | `ChatClientTagsKeyPrefix` (prefix) | Adds a tag to the durable model-activity span |

For custom workflows using `DurableChatClient`, client resolution order is: per-call
`WithChatClientKey`, then `DurableExecutionOptions.DefaultChatClientKey`, then the unkeyed
`IChatClient`. Managed sessions freeze the profile at session creation; see
[Keyed client profiles](#keyed-client-profiles). The keys are
`public const string` members of `TemporalChatOptionsExtensions`. The library strips every
Temporal-owned key before the call reaches the provider; see
[Chat options boundaries](../../architecture/MEAI/durable-chat-pipeline.md#chat-options-boundaries).

### Durable tool calls with `AIFunction.AsDurable()`

> **Most applications that own a custom workflow and want durable LLM calls should start with
> [custom workflow output](custom-workflow-output.md) instead.** `DurableChatWorkflowBase<TOutput>`
> and `DurableToolWorkflowBase<TRequestData, TTurnState>` are backed by a full runnable sample
> (`samples/MEAI/CustomWorkflow`) and give you history, HITL, and continue-as-new for free. If you
> deliberately want none of that session machinery — a workflow that needs to make one or two
> individual LLM/tool calls durable and nothing more — write a small hand-rolled Activity for the
> LLM call (constructor-injected `IChatClient`, one `[Activity]` method, no durable-adapter
> ceremony — see `samples/MEAI/CustomWorkflow`'s `ShoppingActivities` for the reference shape) and
> use `AIFunction.AsDurable()` below for the tool call. The full pattern (Activity + `AsDurable()`
> combined) is a runnable sample: `samples/MEAI/DirectAdapters`.

Wrap a known `AIFunction` with `AsDurable()` to make a single tool call durable from workflow code
that already writes its own Activity for the LLM call — independently of the managed session's
tool contract. It passes through unchanged outside a workflow, and dispatches as a Temporal
activity when called from inside one:

```csharp
[Workflow]
public sealed class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(ResearchRequest request)
    {
        // Look up a fact with a durable tool call — dispatches to DurableFunctionActivities and
        // resolves "get_current_weather" from the worker's AddDurableTool/AddDurableTools registry.
        // AsDurable() always runs on this workflow's own task queue, so its worker must be the
        // same one registering the tool.
        var weatherTool = AIFunctionFactory.Create(
            (string city) => "unreachable stub — only invoked outside a workflow",
            name: "get_current_weather",
            description: "Returns the current weather conditions for a given city.")
            .AsDurable();

        var weather = await weatherTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = request.City }));

        // Feed the tool result into a durable LLM call via a hand-written Activity —
        // ResearchActivities.SummarizeWeatherAsync is constructor-injected with the real,
        // worker-side IChatClient and dispatched as a standard Temporal activity.
        var activityOptions = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) };
        var weatherText = weather?.ToString() ?? string.Empty;
        return await Workflow.ExecuteActivityAsync(
            (ResearchActivities a) => a.SummarizeWeatherAsync(request.City, weatherText),
            activityOptions);
    }
}
```

Register the real provider-side `IChatClient` (constructor-injected into your Activity class),
`AddDurableAI()`, the durable tool, the Activity class (via `AddSingletonActivities<T>`), and
`ResearchWorkflow` itself on the worker — workflow classes receive no application DI services, so
the stub lambda above must never perform provider I/O; only the worker-side registrations are ever
actually invoked. See `samples/MEAI/DirectAdapters` for the complete, runnable wiring.

A few contract points worth knowing up front:

- `AsDurable()` function activities always run on the calling workflow's own task queue,
  regardless of `DurableExecutionOptions.TaskQueue` (which routes the managed session and
  direct chat adapters instead) — its worker must be the same one that registered the
  tool via `AddDurableTools`/`AddDurableTool`. See
  [the direct-adapter task-queue boundary](../../architecture/MEAI/durable-chat-pipeline.md#direct-adapter-task-queue-boundary)
  for the full routing model.
- A hand-written Activity for the LLM call gives you full control over timeouts, heartbeating
  (for long tool-invocation loops), and retry policy via standard `ActivityOptions` — no
  `ChatOptions` extension-method translation layer to reason about.

## Where to go next

**How-to guides**

- [Tool functions](tool-functions.md) — the three durable tool registration levels
  (`AddDurableTools`/`AddDurableToolset`/`AddDurableToolFactory`) and invocation-scoped tools
- [MCP tools](mcp-tools.md) — registering remote MCP client tools through the same durable surface
- [Managed-session tool rules](managed-session-tool-rules.md) — the tool rules
  specific to managed sessions (thin client, worker-resolved manifest)
- [HITL patterns](hitl-patterns.md) — pausing a tool call for human approval
- [Payload codecs](payload-codecs.md) — optional bounded gzip compression for durable payloads
- [Embeddings](embeddings.md) — durable embedding calls in workflow-owned activities (the library
  does not wrap `IEmbeddingGenerator` with `UseDurableExecution()`)
- [Custom workflow output](custom-workflow-output.md) — subclassing `DurableChatWorkflowBase<TOutput>`
  or `DurableToolWorkflowBase<TRequestData, TTurnState>` for structured per-turn output
- [Observability](observability.md) — the `ActivitySource`/`Meter`/`ILogger` surface
- [Testing](testing.md) — unit-testing durable AI code vs. integration-testing against a real server

**Architecture & concepts**

- [Durable chat pipeline](../../architecture/MEAI/durable-chat-pipeline.md) — the managed-session
  pipeline and the direct-adapter task-queue routing model
- [Durable toolsets](../../architecture/MEAI/durable-toolsets.md) — worker-owned toolset
  authority and manifest design
- [Cross-library integration](../../architecture/MEAI/cross-library-integration.md) — using this
  library alongside `TemporalCommunity.Extensions.Agents`
- [Extensible durable turns](../../architecture/MEAI/extensible-durable-turns.md) — receiver and
  scope lifetime rules for `AddDurableToolFactory<THandler>`
- [Durable approvals](../../concepts/durable-approvals.md) — the generic per-request approval
  lifecycle and retry outcomes
- [Security boundary](../../security.md) — normative authentication/authorization rules for
  externally reachable session and approval endpoints
