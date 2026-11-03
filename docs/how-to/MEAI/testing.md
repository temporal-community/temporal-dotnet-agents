# Testing — TemporalCommunity.Extensions.AI

## Two Testing Layers

Durable AI code has two distinct testing concerns, and they call for different tools:

| Layer | What to test | Tool |
|---|---|---|
| **Application logic** | Code that depends on `IDurableChatSessionClient` — controllers, services, background jobs | xUnit unit tests with a stub implementation |
| **Library integration** | The workflow, activities, and middleware together — that the full pipeline produces correct results | xUnit integration tests using `TemporalServiceTestEnvironment.StartLocalAsync()` |

## Phase 2.2 turn-admission verification

The managed-session regression
`DurableChatLifecycleRegressionTests.TurnAdmission_RejectsBusyAndConflictBeforeAccepting_AndJoinsMatchingRequest`
holds the model activity open, then verifies:

- a different request ID is rejected as `DurableTurnBusy`;
- reusing the active request ID with changed input is rejected as `DurableTurnConflict`;
- neither rejection adds an Update-Accepted event;
- a matching request is accepted as a joiner, both callers receive the same result, and only one
  model call is made.

`TurnAdmission_ReplaysTerminalFailureWithoutRedispatchingModelActivity` exercises rollback and
terminal failure replay: the failed turn leaves no partial history, and retrying the same ID does
not start another turn. Activity retry attempts within the original turn are still governed by the
configured Temporal retry policy. Turn cancellation is a later Phase 3.5 capability.

## Phase 2.3 bounded-ledger and continue-as-new verification

`DurableChatLifecycleRegressionTests.KeyedReducer_ContinueAsNew_RetainsLaterSuccessfullyCompletedTurn`
also retries the completed request after Continue-as-New and verifies the model is not called again.
It checks that the session epoch and cumulative completed-turn count survive the transition.
`TerminalOutcomeLedger_EvictsOldestOutcomeAtConfiguredCount` and
`TerminalOutcomeLedger_DoesNotRetainAnEntryLargerThanByteLimit` exercise both configured ledger
bounds and confirm that eviction ends the deduplication window. Unit tests cover option validation,
workflow-input serialization, and preservation of the ledger settings and epoch in Continue-as-New.

Custom workflows based on `DurableToolWorkflowBase<TRequestData, TTurnState>` must call
`ValidateDurableTurnAdmission` from the validator paired with each turn Update. Handler-side checks
remain defensive, but only a validator can reject a request before the Update is recorded. The
`ExtensibleDurableTurns` sample and its compiled usage-guide snippet demonstrate this pairing.

## Phase 0 research and harness

The [redesign target contracts](usage.md#durable-chat-redesign-target-contracts) are not production
acceptance claims. Phase 0 executes package contracts against MEAI 10.10.0 and Temporalio 1.20.0:

```bash
dotnet test tests/TemporalCommunity.Extensions.AI.Tests --no-restore \
  --filter "FullyQualifiedName~PinnedReducerContractTests"
```

`PinnedReducerContractTests` covers counting exclusions/first-system retention, summarizer
thresholds, dropped-input mutation, unmarked summary output, reuse of original history, and
exclusion of function and approval content from summarizer input. It also checks the SDK's
integer history-count/byte-size properties and validator attribute. Runtime validation of the
event-budget assumptions belongs to the isolated harness below, not to reflection alone.

Evidence sources: read-only MEAI `ChatReduction` sources and Temporal SDK `Workflow`/validator
sources, plus executed pinned NuGet assemblies. The MEAI source checkout HEAD was
`4332a6910796b2496f9cc35c06fa362671ee1b2f`, not the earlier spike's
`02107c65bab30aad9e35b5133ed643eaa77bccd8`; package execution is authoritative.
Neither reference checkout is built or modified.

**Unavailable prerequisite experiment:** the assumption that the pinned summarizer returns a
persistent incremental marker is false. This blocks adopting that marker as a compaction wire
contract, not Phase 0 or the library-selected-prefix summarizer design. The history-counter APIs
are available; their values reflect the current workflow task and may be stale in queries.

### Spike findings and deferred acceptance

The earlier uncommitted reducer spike reported **23 passing research tests**; the synthetic
session-core spike reported **4 passing research tests**. Those counts are historical evidence,
not checked-in production acceptance or a claim that their source was recovered.
Phase 0 replaces the faulty persistent-summary-marker assumption with
`PinnedReducerContractTests`, rather than importing that prototype. No prototype implementation
is promoted to the library.

`SessionCoreResearchTests` recreates the initialization race with an explicit workflow-state
barrier, not a wall-clock sleep: the run waits for an update to enter before initializing state.
The unsafe handler reads uninitialized state; an asynchronous handler that waits for initialization
sees initialized state. Both executions replay. This is a controlled scheduling counterexample,
not proof that the current stock session or every update-with-start always has this ordering.
Current custom workflows already have `InitializeInput`/`WaitForInputAsync`; the new session core
still needs its own authoritative initialization/admission design. Validators cannot await.

```bash
dotnet test tests/TemporalCommunity.Extensions.AI.IntegrationTests --no-restore \
  --filter "FullyQualifiedName~SessionCoreResearchTests"
```

The skipped `RedesignedSessionCore_UpdateWithStart_CannotReadUninitializedState` names **unit 2.2**
as its owner. Its counterexample deliberately fails if unskipped without replacing the synthetic
unsafe path with the production redesigned core. It is intentionally unimplemented acceptance,
not an unavailable pinned API and not a Phase 0 failure. Absolute idle-deadline renewal and
expected-epoch restart behavior are covered by the Phase 2.4 lifecycle tests. Approval waits (3.3)
and complete MEAI dispatch (1.4/3.2) remain later-unit acceptance work; Phase 0 does not add
placeholder passing tests for them.

The target session declares **no signal handlers**. Current `Shutdown` is replaced by validated
`RequestShutdown` in **2.6**, including both clients, samples, smoke tests, integration tests, and
documentation; `ShutdownSignalName` is removed there, not during Phase 0.

### Deterministic doubles and isolated environments

Reuse `ScriptedChatClient` from `TemporalCommunity.Extensions.Tests.Shared`: every test owns its
response queue; exhaustion is a non-retryable failure, not a default success. It captures detached
JSON message snapshots and cloned `ChatOptions` (nested option values still follow MEAI's clone
semantics). Cancellation before invocation does not consume a response. Avoid concurrent calls
when the test requires a specific provider-response order.

`ControlledActivityAttempt<T>` separates entry/side effects from completion, failure, and
cancellation. Each retry gets a distinct attempt. Await `Entered` with a finite test-side bound
before asserting an effect or releasing completion. A failed or cancelled response does not undo
an already applied effect. These `TaskCompletionSource` gates are for activities/tools/providers,
**never workflow code**; workflow barriers use `Workflow.WaitConditionAsync`.

`Helpers/DurableChatTestEnvironment` starts a fresh server and sets
`DurableAIDataConverter.Instance` on its client before any worker or handle is created. The worker
inherits the client's converter in Temporalio 1.20.0 (there is no
`TemporalWorkerOptions.DataConverter` property). Dispose each environment with `await using`;
use a unique task queue and workflow ID. Do not mutate the shared collection's provider scripts
for race tests.

```bash
dotnet test tests/TemporalCommunity.Extensions.AI.Tests --no-restore \
  --filter "FullyQualifiedName~DeterministicHarnessContractTests"
dotnet test tests/TemporalCommunity.Extensions.AI.IntegrationTests --no-restore \
  --filter "FullyQualifiedName~PhaseZeroHarnessIntegrationTests"
```

The unit checks exercise deterministic provider output, captured inputs, cancellable controlled
tools, and approval/function exchange round-trips. Pinned `AIFunctionFactory` returns a
`JsonElement` for this string tool result; assertions inspect its string value.
The isolated runtime checks prove a three-day workflow timer skips in bounded real test time
and replays, controlled retries can duplicate an external effect, and worker restart/replay
does not re-execute a completed activity. The timer's recorded duration is exactly three days;
workflow-clock elapsed time includes task scheduling overhead and is not exactly the delay.

On the idle local service, **ten validator rejections persist zero new events**, verified by
comparing the full event-ID/type sequence before and after. Accepted updates grow both history
count and byte size, and their history replays. This verifies a prerequisite, not a universal
guarantee that rejection consumes no server CPU, RPC traffic, or workflow tasks alongside other
work. Budget logic must use task-boundary snapshots, not query freshness or local rejection
counters.

`GetSystemInfo` reports **1.31.2** for the pinned local server. The SDK time-skipping server
returns an **empty server version**; the harness reports that explicitly and does not claim it
meets the production minimum. A versioned production-service experiment on that timer-only
server is unavailable; transport, restart, retry, and validator-history prerequisites are
verified on the version-checked local service instead.

### Phase 0 timer harness

This compile-checked example uses the repository's shared test project (not a separately
published NuGet package). For application tests, copy the pinned environment helper or reference
your own equivalent. Use it only for workflow-timer behavior, with no provider/tool activities.

```csharp
await using var environment = await TemporalServiceTestEnvironment.StartTimeSkippingAsync();
environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
var queue = $"timer-example-{Guid.NewGuid():N}";
using var worker = new TemporalWorker(environment.Client,
    new TemporalWorkerOptions(queue).AddWorkflow<TimerExampleWorkflow>());

await worker.ExecuteAsync(async () =>
{
    var handle = await environment.Client.StartWorkflowAsync(
        (TimerExampleWorkflow workflow) => workflow.RunAsync(TimeSpan.FromDays(3)),
        new($"timer-example-{Guid.NewGuid():N}", queue));
    var elapsed = await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Assert.True(elapsed >= TimeSpan.FromDays(3));
});
```

```csharp
[Workflow]
public class TimerExampleWorkflow
{
    [WorkflowRun]
    public async Task<TimeSpan> RunAsync(TimeSpan delay)
    {
        var started = Workflow.UtcNow;
        await Workflow.DelayAsync(delay);
        return Workflow.UtcNow - started;
    }
}
```

### Phase 0 controlled tool attempt

The shared `ControlledActivityAttempt<T>` makes an effect observable before returning a result,
without sleeping. Use separate attempts to model retries, and always release blocked attempts in
test cleanup. MEAI's factory normalizes this string result to `JsonElement`.

```csharp
var attempt = new ControlledActivityAttempt<string>();
var effects = 0;
var tool = AIFunctionFactory.Create(
    (CancellationToken token) => attempt.ExecuteAsync(
        () => Interlocked.Increment(ref effects), token),
    "controlled_write");
var pending = tool.InvokeAsync(new AIFunctionArguments()).AsTask();
try
{
    await attempt.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(1, effects);
    Assert.False(pending.IsCompleted);
    attempt.Complete("written");
    Assert.Equal("written", Assert.IsType<JsonElement>(await pending).GetString());
}
finally
{
    if (!attempt.IsCompletionSupplied)
        attempt.Complete("cleanup");
}
```

The rule of thumb: if your code just *calls* `SendAsync` or `GetHistoryAsync`, unit test it with a stub. If you are verifying that conversation history accumulates correctly or that `ContinueAsNew` works, use an integration test.

For `DurableToolWorkflowBase`, integration tests should separately prove the one-managed-turn-per-
Update guard and Continue-as-New preservation of the concrete workflow plus frozen declaration and
policy snapshot. The repository's `TypedDurableTurnLifecycleTests` is the reference shape.

Workflow-command compatibility is covered by checked-in histories. Capture the typed history only
when its command sequence intentionally changes:

```bash
dotnet test tests/TemporalCommunity.Extensions.AI.IntegrationTests \
  --filter "FullyQualifiedName~HistoryCaptureTests.Capture_TypedDurableTurn"
dotnet test tests/TemporalCommunity.Extensions.AI.Tests \
  --filter "FullyQualifiedName~WorkflowReplayTests"
```

Inspect the captured history before committing it. The typed fixture must contain one model
activity, one real tool activity with state completion, a final model activity, and one accepted and
completed Update. Ordinary test runs exclude `Category=HistoryCapture` so they never rewrite the
checked-in corpus.

Every checked-in history must also have an entry in its package's
`Compat/replay-fixture-dispositions.json` catalog under `tests/TemporalCommunity.Extensions.*.Tests`.
The unit-test catalogs reject both unclassified histories and stale disposition entries. A successful replay
fixture must retain a focused replay test; an expected-nondeterminism fixture must retain a negative
test proving that replay fails for the intended reason. Do not delete a replay consumer while
regenerating histories for an unrelated feature change.

Before adopting or publishing the typed API, run its clean NuGet consumer:

```bash
just smoke-extensible-turns
```

This gate does not use project references or the normal global NuGet cache. It validates the local
source metadata and SHA-512 of the freshly packed AI and Agents packages, then runs the public
split-registration workflow once with `lib/net10.0` and once with `lib/netstandard2.1` selected.
The client registers no tool or schema. The worker owns the named toolset, and the run proves
manifest resolution, approval and denial, activity retry with fresh/disposed scoped services,
sequential state, and separate resolver/model/tool activities.

---

## Unit Testing Application Code

### Stub `IDurableChatSessionClient`

`DurableChatSessionClient` is thin Temporal protocol infrastructure — it adapts `SendAsync` calls to workflow updates. Testing it directly (by mocking `ITemporalClient` and asserting `ExecuteUpdateAsync` was called) only verifies the SDK's API surface, not your business logic.

The right move is to write your application code against `IDurableChatSessionClient` and inject a stub in tests:

```csharp
// Client
// Production service
public class ConversationService(IDurableChatSessionClient client)
{
    public async Task<string> AskAsync(string sessionId, string question)
    {
        var response = await client.SendAsync(
            sessionId,
            [new ChatMessage(ChatRole.User, question)]);

        return response.Text ?? string.Empty;
    }
}
```

```csharp
// Test
// Stub for unit tests. Signatures match IDurableChatSessionClient post-0.2.0:
// SendAsync returns DurableSessionResponse (carries per-turn Usage / CorrelationId);
// GetHistoryAsync returns IReadOnlyList<DurableSessionEntry>.
public class StubChatSessionClient : IDurableChatSessionClient
{
    public Func<string, IEnumerable<ChatMessage>, ChatOptions?, string?, CancellationToken, Task<DurableSessionResponse>>
        SendAsyncHandler { get; set; } = (_, _, _, _, _) =>
            Task.FromResult(new DurableSessionResponse
            {
                CorrelationId = "stub",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = [new ChatMessage(ChatRole.Assistant, "stub reply")],
            });

    public Task<DurableSessionResponse> SendAsync(string conversationId, IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, string? correlationId = null,
        CancellationToken cancellationToken = default)
        => SendAsyncHandler(conversationId, messages, options, correlationId, cancellationToken);

    public Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DurableSessionEntry>>([]);

    public Task<DurableApprovalRequest?> GetPendingApprovalAsync(string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<DurableApprovalRequest?>(null);

    public Task<DurableApprovalResolutionResult> ResolveApprovalAsync(string conversationId,
        DurableApprovalDecision decision, CancellationToken cancellationToken = default)
        => Task.FromResult(new DurableApprovalResolutionResult
        {
            RequestId = decision.RequestId,
            Status = DurableApprovalResolutionStatus.Accepted,
        });
}
```

```csharp
// Test
[Fact]
public async Task AskAsync_Returns_AssistantText()
{
    var stub = new StubChatSessionClient();
    stub.SendAsyncHandler = (_, _, _, _, _) =>
        Task.FromResult(new DurableSessionResponse
        {
            CorrelationId = "test-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.Assistant, "Paris.")],
        });

    var service = new ConversationService(stub);
    var result  = await service.AskAsync("conv-1", "What is the capital of France?");

    Assert.Equal("Paris.", result);
}
```

Register the stub in DI with the interface type:

```csharp
// Test
services.AddSingleton<IDurableChatSessionClient, StubChatSessionClient>();
```

---

## Integration Testing with `WorkflowEnvironment`

Integration tests use `TemporalServiceTestEnvironment.StartLocalAsync()`, which pins Temporal CLI
`v1.8.0`, starts its embedded Temporal Server 1.31.2 inside the test process, and verifies the
reported service version through `GetSystemInfo`. No external `temporal server start-dev` is
required; the server starts and stops with the test suite.

### Test direct middleware through a registered workflow

`UseDurableExecution()` has two execution paths selected by `Workflow.InWorkflow`. Calling
`DurableChatActivities` directly tests the activity implementation, but it does not execute the
middleware's workflow path or verify that its continuation stays on Temporal's workflow task
scheduler.

For a direct-adapter integration test:

1. Register a real `[Workflow]` type and the backing activities on a local worker.
2. Construct `ChatClientBuilder` with a sentinel inner client that throws if invoked, then apply
   `UseDurableExecution()` inside the workflow. This is legacy direct-adapter test coverage, not the recommended custom-workflow
   design (use a hand-written model activity or the session base classes for new applications).
3. Invoke `GetResponseAsync` from `[WorkflowRun]`. `GetStreamingResponseAsync` is intentionally
   unsupported in workflow context and must be tested by advancing its async enumerator and
   asserting the resulting `NotSupportedException`.
4. Issue another workflow command after the call, such as `Workflow.DelayAsync`. This proves the
   continuation can still use the workflow scheduler; observing only the activity result is not
   sufficient.
5. Put a finite `WaitAsync` bound around `handle.GetResultAsync()` so a scheduler regression fails
   the test instead of hanging the test process.
6. Inspect workflow history and assert the expected activity and post-call timer were scheduled.

Register the real provider-side `IChatClient` on the worker. The workflow-local sentinel exists
only to prove that provider I/O was dispatched through the activity. Streaming is not implemented
across the workflow/activity boundary, so it fails before an activity is scheduled rather than
silently changing into a buffered response.

### NuGet packages

```xml
<PackageReference Include="Temporalio" Version="1.20.0" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" />
```

### Fixture pattern

Share the `WorkflowEnvironment` and the hosted worker across all tests in a class via `IClassFixture<T>`. Starting a local server takes a couple of seconds — sharing it amortizes that cost.

```csharp
// Test
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "test-durable-ai";
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public TestChatClient ChatClient { get; } = new();
    public DurableChatSessionClient SessionClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TemporalServiceTestEnvironment.StartLocalAsync();
        Environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);
        builder.Services.AddSingleton<IChatClient>(ChatClient);

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout   = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout  = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        _host = builder.Build();
        await _host.StartAsync();
        SessionClient = _host.Services.GetRequiredService<DurableChatSessionClient>();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        await Environment.DisposeAsync();
    }
}
```

### `TestChatClient` — the `IChatClient` stub

Register a deterministic `IChatClient` stub so tests are not coupled to a live LLM. The pattern used in this library's own integration tests:

```csharp
// Test
public sealed class TestChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;
    public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
    public string ResponsePrefix { get; set; } = "Response";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (ReceivedMessages) { ReceivedMessages.Add(list); }
        Interlocked.Increment(ref _callCount);

        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "(empty)";

        return Task.FromResult(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, $"{ResponsePrefix}: {lastUser}")])
        {
            ModelId = "test-model",
            Usage = new UsageDetails
            {
                InputTokenCount  = lastUser.Length,
                OutputTokenCount = lastUser.Length + ResponsePrefix.Length + 2,
            },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
```

`ResponsePrefix` lets individual tests vary the reply without creating a new stub class.

### Writing integration tests

```csharp
// Test
public class DurableChatSessionTests(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task SendAsync_Returns_AssistantResponse()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";

        var response = await fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        Assert.Equal("Response: Hello", response.Text);
    }

    [Fact]
    public async Task MultiTurn_Accumulates_History()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";

        await fixture.SessionClient.SendAsync(conversationId,
            [new ChatMessage(ChatRole.User, "First message")]);

        await fixture.SessionClient.SendAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Second message")]);

        var history = await fixture.SessionClient.GetHistoryAsync(conversationId);

        // Each turn retains a request entry and a response entry, not individual messages.
        Assert.Equal(4, history.Count);
        Assert.IsType<DurableSessionRequest>(history[0]);
        Assert.IsType<DurableSessionResponse>(history[1]);
    }

    [Fact]
    public async Task Same_ConversationId_Reuses_Workflow()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";
        int callsBefore = fixture.ChatClient.CallCount;

        await fixture.SessionClient.SendAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Turn 1")]);
        await fixture.SessionClient.SendAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Turn 2")]);

        // Two SendAsync calls → two LLM calls, one workflow
        Assert.Equal(callsBefore + 2, fixture.ChatClient.CallCount);
    }
}
```

> **Unique conversation IDs per test:** Always generate a fresh `conversationId` (e.g., `Guid.NewGuid()`) in each test. Tests sharing a conversation ID share workflow state — history from one test leaks into another.

---

## What NOT to Test

**Don't mock `ITemporalClient` to assert SDK calls.** Tests like "verify that `StartWorkflowAsync` was called with `WorkflowIdConflictPolicy.UseExisting`" only verify the Temporal SDK's API surface. They don't catch bugs in serialization, history management, or retry behavior. Use integration tests for those.

**Don't unit test `DurableChatSessionClient` directly.** It is thin infrastructure — a Temporal protocol adapter. Its correct behavior is proven by the integration tests. Making application code that *depends on it* testable is the purpose of `IDurableChatSessionClient`.

**Don't use a real LLM in integration tests.** LLM responses are non-deterministic, slow, and cost money. The `TestChatClient` pattern gives you full control over what the "LLM" returns, making assertions reliable.

---

## Running the Tests

```bash
# Unit tests — no server required
dotnet test tests/TemporalCommunity.Extensions.AI.Tests

# Integration tests — uses embedded Temporal server (no external process needed)
dotnet test tests/TemporalCommunity.Extensions.AI.IntegrationTests \
  --filter "Category!=HistoryCapture"

# All tests
just test
```

> The replay fixtures under `tests/TemporalCommunity.Extensions.AI.Tests/Compat/Histories/` are
> produced by tests tagged
> `Category=HistoryCapture`, which `File.WriteAllTextAsync` over the checked-in JSON rather than
> asserting against it. The public `just test` and `just test-integration` recipes and diagnostic
> helpers `_test-individual` and `_test-logged` pass `--filter "Category!=HistoryCapture"`.
> `capture-histories` is the intentional exception and runs both libraries' history-capture tests,
> regenerating replay fixtures for MEAI and MAF. When
> running the library project directly, use the filter shown above; otherwise those capture tests
> run. The symptom is a pile of unexplained fixture diffs; the fix is
> `git checkout -- tests/*/Compat/Histories`. Regenerate both corpora on purpose with
> `just capture-histories` only when a workflow's command sequence has genuinely changed.
>
> Any new ordinary test recipe that runs `dotnet test` against an integration project must carry
> that exclusion — only the explicit history-capture recipes intentionally select the category.

Both test suites use an embedded Temporal Server 1.31.2 — no separate `temporal server start-dev`
process is needed. AI integration tests use `TemporalServiceTestEnvironment.StartLocalAsync()`;
Agents integration tests use `TestEnvironmentHelper.StartLocalAsync()`, which delegates to the
same pinned/version-checked helper and pre-registers the `AgentName`, `SessionCreatedAt`, and
`TurnCount` search attributes enabled by default. Do not add bare
`WorkflowEnvironment.StartLocalAsync()` calls, because they silently float the tested server.

Pull requests run both workflows with read-only token permissions. The integration matrix lists the
two suites literally in `.github/workflows/integration.yml`, so **adding a third integration project
means adding two lines there** — nothing discovers it for you. Each job restores and builds only
that project, excludes `Category=HistoryCapture`, applies a four-minute per-test hang limit and a
twenty-minute job limit, and always uploads TRX results. The workflow caches Temporal CLI v1.8.0 by
OS/architecture, verifies the official release SHA-256 before extracting it, and supplies the
verified executable through `TEMPORAL_TEST_SERVER_PATH`.

Use `TemporalServiceTestEnvironment.StartTimeSkippingAsync()` only when the behavior under test is
defined entirely by workflow timers. Keep transport, worker restart, activity retry, and real
server behavior on `StartLocalAsync()`.
