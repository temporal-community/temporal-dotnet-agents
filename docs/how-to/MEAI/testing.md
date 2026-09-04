# Testing — TemporalCommunity.Extensions.AI

## Two Testing Layers

Durable AI code has two distinct testing concerns, and they call for different tools:

| Layer | What to test | Tool |
|---|---|---|
| **Application logic** | Code that depends on `IDurableChatSessionClient` — controllers, services, background jobs | xUnit unit tests with a stub implementation |
| **Library integration** | The workflow, activities, and middleware together — that the full pipeline produces correct results | xUnit integration tests using `WorkflowEnvironment.StartLocalAsync()` |

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
   `UseDurableExecution()` inside the workflow.
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
<PackageReference Include="Temporalio.Testing" Version="1.11.1" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
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
        Environment = await WorkflowEnvironment.StartLocalAsync();

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
        await Environment.ShutdownAsync();
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

        // user + assistant + user + assistant = 4 messages
        Assert.Equal(4, history.Count);
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal(ChatRole.Assistant, history[1].Role);
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
just test-unit-ai

# Integration tests — uses embedded Temporal server (no external process needed)
just test-integration-ai

# All tests
just test
```

Both test suites use an embedded Temporal Server 1.31.2 — no separate `temporal server start-dev`
process is needed. AI integration tests use `TemporalServiceTestEnvironment.StartLocalAsync()`;
Agents integration tests use `TestEnvironmentHelper.StartLocalAsync()`, which delegates to the
same pinned/version-checked helper and pre-registers the `AgentName`, `SessionCreatedAt`, and
`TurnCount` search attributes enabled by default. Do not add bare
`WorkflowEnvironment.StartLocalAsync()` calls, because they silently float the tested server.

Pull requests run both workflows with read-only token permissions. Integration projects are
discovered from `tests/*IntegrationTests/*.csproj`; adding a project automatically adds a matrix
job. Each job restores and builds only that project, excludes `Category=HistoryCapture`, applies a
four-minute per-test hang limit and a twenty-minute job limit, and always uploads TRX results.
The workflow caches Temporal CLI v1.8.0 by OS/architecture, verifies the official release SHA-256
before extracting it, and supplies the verified executable through `TEMPORAL_TEST_SERVER_PATH`.
Run `tests/ci/discover-integration-projects.test.sh` locally after changing discovery behavior.

Use `TemporalServiceTestEnvironment.StartTimeSkippingAsync()` only when the behavior under test is
defined entirely by workflow timers. Keep transport, worker restart, activity retry, and real
server behavior on `StartLocalAsync()`.
