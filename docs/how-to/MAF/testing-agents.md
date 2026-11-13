# Testing Agents

How to test Temporal Agents applications, from unit tests without a Temporal server to integration tests against the SDK's local service. Examples that use internal test helpers are labeled as repository-only.

---

## Table of Contents

1. [Overview](#overview)
2. [Unit Testing (No Temporal Server)](#unit-testing-no-temporal-server)
3. [Integration Testing (Real Temporal Server)](#integration-testing-real-temporal-server)
4. [Running Tests](#running-tests)

---

## Overview

The test suite is split into two projects:

| Project | Server Required | Purpose |
|---------|-----------------|---------|
| `TemporalCommunity.Extensions.Agents.Tests` | No | Configuration, routing, DI registration, serialization, and workflow replay |
| `TemporalCommunity.Extensions.Agents.IntegrationTests` | Yes | Agent execution, approval flows, continue-as-new, and worker behavior |

**General principles:**

- **Hand-written test doubles** for agent and chat-client behavior; use FakeItEasy for broader interfaces when a hand-written implementation adds little
- **xUnit** with `[Fact]` attributes and `Assert.*` assertions
- **Exact exception types** — `Assert.Throws<T>` matches the exact type (`ArgumentNullException`, not `ArgumentException`)

---

## Unit Testing (No Temporal Server)

### StubAIAgent — Minimal Test Double

`StubAIAgent` is a concrete `AIAgent` that returns a fixed response without calling any LLM. It's the workhorse of the unit test suite:

```csharp
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents;
using TemporalCommunity.Extensions.Agents.Session;

internal sealed class StubAIAgent(
    string? name,
    AgentResponse? fixedResponse = null,
    string? description = null) : AIAgent
{
    private readonly AgentResponse _fixedResponse = fixedResponse ?? new AgentResponse();

    public override string? Name { get; } = name;
    public override string? Description { get; } = description;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default) =>
        new(new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey(Name ?? "stub")));

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_fixedResponse);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This stub does not serialize sessions.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This stub does not deserialize sessions.");

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This stub supports only non-streaming runs.");
}
```

**Usage in a workflow test:**

```csharp
// StubAIAgent returns a fixed classification keyword — no LLM required.
private static StubAIAgent ClassifierReturning(string keyword) =>
    new("Classifier", new AgentResponse(new ChatMessage(ChatRole.Assistant, keyword)));

[Fact]
public async Task StubAIAgent_ReturnsFixedResponse()
{
    var agent = ClassifierReturning("ORDERS");
    Assert.Equal("Classifier", agent.Name);
    Assert.Equal("ORDERS", (await agent.RunAsync("classify")).Text);
}
```

### CapturingChatClient — Spy for ChatOptions

`CapturingChatClient` records the `ChatOptions` from each call, letting you assert that middleware propagates options correctly:

```csharp
internal sealed class CapturingChatClient : IChatClient
{
    public List<ChatOptions?> CapturedOptions { get; } = [];
    public ChatOptions? LastOptions => CapturedOptions.LastOrDefault();

    // The durable-agent path invokes the model through agent.RunAsync, which calls GetResponseAsync.
    // A double that captures only in GetStreamingResponseAsync does not exercise this path.
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CapturedOptions.Add(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This spy supports only non-streaming calls.");

    public void Dispose() { }
}
```

> Implement `GetResponseAsync` for the durable-agent path. `RunStreamingAsync` is not supported.
> See [Intercepting LLM Calls](./llm-call-interception.md) for the interception boundary.

### Test custom middleware leaf preservation

A custom `ConfigureAgentPipeline` wrapper should preserve the factory's supplied inner agent through
`DelegatingAIAgent`. Assert the wrapper's actual `InnerAgent`, rather than merely retaining the
constructor argument in a separate property:

```csharp
using Microsoft.Agents.AI;
using TemporalCommunity.Extensions.Agents.Testing;
using Xunit;

public sealed class MiddlewareTests
{
    private sealed class TestMiddleware(AIAgent inner) : DelegatingAIAgent(inner)
    {
        internal AIAgent ForwardedAgent => InnerAgent;
    }

    [Fact]
    public void Middleware_PreservesSuppliedInnerAgent()
    {
        var inner = NoOpAgent.Instance;
        var middleware = new TestMiddleware(inner);

        Assert.Same(inner, middleware.ForwardedAgent);
    }
}
```

Also run worker-startup configuration tests with the real `ConfigureAgentPipeline` callback. The
library rejects factories that return an unrelated agent and opaque `AIAgent` subclasses that hide
their forwarding target. A short-circuiting middleware is valid as long as its structural chain
still preserves the supplied inner agent.

`ConfigureAgentPipeline` is validated at worker startup. Live pipelines are built inside activity scopes, not cached per
worker: provider preparation, each model-step attempt (including retries), turn completion, and
the Temporal chat-history memory-search path each build their own pipeline. Middleware factories
receive the activity's scoped service provider. Custom wrappers themselves must be non-disposable;
inject disposable dependencies from the scope and let that scope own them. If the pipeline uses MAF's built-in
`OpenTelemetryAgent`, verify that no new telemetry is emitted through that exact wrapper after the
activity-owned pipeline is disposed. Cover success, provider failure, cancellation, and retries on
the activity paths your pipeline uses: each attempt should build a fresh pipeline. The library
disposes its `OpenTelemetryAgent` wrappers; custom wrappers must remain non-disposable. Also test
topology rejection with a real `OpenTelemetryAgent`; the library disposes it if validation rejects
the completed chain. If an `AIAgentBuilder` factory throws while `Build()` is assembling a chain,
MAF does not return the partial chain to the caller, so the library cannot discover wrappers
already created inside it.

For session-aware middleware, run the real activity path and assert that the middleware receives
the `TemporalAgentSession` instance also exposed through
`AIAgent.CurrentRunContext.Session` and `TemporalAgentContext.Current.CurrentSession` around
`next`. A StateBag mutation is returned in `AgentStepResult.StateBagDelta`; the workflow applies
that delta before dispatching the next step. Also verify that exceptions and cancellation restore
the durable context for surrounding middleware, and that replacing the session fails before the
chat-client leaf runs. The leaf may observe MAF's transient `ChatClientAgentSession`; outer
middleware continues to see the durable session. Streaming through the Temporal agent proxy is not
supported; durable runs use `RunAsync` and return a completed response.

### Testing TemporalAgentsOptions Configuration

The fluent `.AddTemporalAgents()` API registers workflows, activities, keyed proxies, and the agent
client. This registration test uses FakeItEasy only to satisfy dependencies; it does not run a
workflow. In application tests, use your preferred `ITemporalClient` and `IChatClient` doubles:

```csharp
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

public sealed class TemporalAgentsOptionsTests
{
    [Fact]
    public void AddTemporalAgents_RegistersKeyedAIAgentProxies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddChatClient(A.Fake<IChatClient>());

        services
            .AddHostedTemporalWorker("test-queue")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("AgentA", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>());
                opts.AddDurableAgent("AgentB", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>());
            });

        using var sp = services.BuildServiceProvider();

        var proxyA = sp.GetTemporalAgentProxy("AgentA");
        var proxyB = sp.GetTemporalAgentProxy("AgentB");

        Assert.NotNull(proxyA);
        Assert.NotNull(proxyB);
        Assert.Same(proxyA, sp.GetTemporalAgentProxy("AgentA"));
    }

    [Fact]
    public void AddTemporalAgents_ThrowsOnNullConfigure()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());

        var builder = services.AddHostedTemporalWorker("q");

        Assert.Throws<ArgumentNullException>(
            () => builder.AddTemporalAgents(null!));
    }
}
```

### Testing Routing Activities

If your application defines routing activities such as `GetAvailableAgents` or `ValidateAgent`,
their business logic is ordinary C# and can be tested directly without a Temporal server. These
are application-defined examples, not APIs supplied by Temporal Agents:

```csharp
[Fact]
public void GetAvailableAgents_ReturnsRegisteredDescriptors()
{
    var options = BuildOptionsWithAgents();  // helper that builds TemporalAgentsOptions
    var activities = new RoutingActivities(options);

    var agents = activities.GetAvailableAgents();

    Assert.Contains(agents, a => a.Name == "OrdersAgent");
    Assert.Contains(agents, a => a.Name == "TechSupportAgent");
}

[Fact]
public void ValidateAgent_KnownName_ReturnsName()
{
    var activities = new RoutingActivities(BuildOptionsWithAgents());
    Assert.Equal("OrdersAgent", activities.ValidateAgent("OrdersAgent", "GeneralAgent"));
}

[Fact]
public void ValidateAgent_UnknownName_ReturnsFallback()
{
    var activities = new RoutingActivities(BuildOptionsWithAgents());
    Assert.Equal("GeneralAgent", activities.ValidateAgent("HallucinatedAgent", "GeneralAgent"));
}
```

### Testing internal StateBag serialization

The following is a repository unit-test pattern: `AgentStepInput` and `AgentStepResult` are
internal implementation types, not package APIs. `AgentStepInput.SerializedStateBag` carries the
serialized session StateBag; `AgentStepResult.StateBagDelta` carries key-level changes from an
activity. Use `AgentSessionStateBag.SetValue` and verify the restored value:

```csharp
[Fact]
public void AgentStepInput_WithStateBag_RoundTrips()
{
    var bag = new AgentSessionStateBag();
    bag.SetValue("key1", "value1");

    var input = new AgentStepInput
    {
        AgentName           = "TestAgent",
        Request             = new RunRequest("hello"),
        AccumulatedMessages = [],
        SerializedStateBag  = bag.Serialize(),
    };

    var json = JsonSerializer.Serialize(input);
    var deserialized = JsonSerializer.Deserialize<AgentStepInput>(json)!;
    Assert.True(deserialized.SerializedStateBag.HasValue);
    var restoredBag = AgentSessionStateBag.Deserialize(deserialized.SerializedStateBag.Value);

    Assert.Equal("value1", restoredBag.GetValue<string>("key1"));
}

[Fact]
public void TemporalAgentSession_SerializeStateBag_EmptyBag_ReturnsNull()
{
    var session = new TemporalAgentSession(
        TemporalAgentSessionId.WithRandomKey("test"));

    // SerializeStateBag() is internal; the library grants its unit-test assembly access
    // through InternalsVisibleTo in the library project file.
    // Empty bags serialize to null — no wasted payload.
    Assert.Null(session.SerializeStateBag());
}
```

---

## Integration Testing (Real Temporal Server)

### IntegrationTestFixture Pattern

This fixture pattern is from the repository's integration tests. `TestEnvironmentHelper` is
internal test-project code, not a package API; it pins and verifies the local server, registers the
required search attributes, and configures the agent converter. For an application-owned fixture,
use the SDK setup in [Configure a local Temporal server](./dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests).
The fixture can be shared across tests in a class via `IClassFixture<>`:

```csharp
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "integration-test-agents";
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public AIAgent AgentProxy { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Repository-only helper: pins the local server, registers search attributes,
        // and configures TemporalAgentDataConverter.
        Environment = await TestEnvironmentHelper.StartLocalAsync();

        _host = BuildHost();
        await _host.StartAsync();

        AgentProxy = _host.Services.GetTemporalAgentProxy("EchoAgent");
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);

        builder.Services.AddChatClient(new EchoChatClient());

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddTemporalAgents(options =>
                options.AddDurableAgent("EchoAgent", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>()));

        return builder.Build();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await Environment.ShutdownAsync();
    }
}
```

### Custom Test Agents

These are repository test doubles, not package types. `TestAgentBase` provides session management
and a shared echo-response helper for some `AIAgent` fakes; `EchoAIAgent` is a standalone
implementation, and the integration tests also use `IChatClient` doubles.

| Agent | Behavior | Tests |
|-------|----------|-------|
| `EchoAIAgent` | Returns `"Echo [{turnCount}]: {lastMessage}"` | History rebuild, multi-turn conversations |
| `SlowThenFastAIAgent` | First N calls delay, then instant | Activity timeout enforcement |
| `FailThenSucceedAIAgent` | First N calls throw, then succeed | Retry mechanism, error propagation |
| `EmptyResponseAIAgent` | Returns empty `AgentResponse` | Edge-case response handling |

**Example — shared echo-response helper:**

```csharp
protected static AgentResponse CreateEchoResponse(IEnumerable<ChatMessage> messages)
{
    var userMessages = messages.Where(m => m.Role == ChatRole.User).ToList();
    int turnCount = userMessages.Count;
    string lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";

    return new AgentResponse
    {
        Messages = [new ChatMessage(ChatRole.Assistant, $"Echo [{turnCount}]: {lastMessage}")],
        CreatedAt = DateTimeOffset.UtcNow
    };
}
```

### Single-Turn and Multi-Turn Tests

```csharp
public class AgentIntegrationTests(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task SingleTurn_ReturnsEchoResponse()
    {
        var session = await fixture.AgentProxy.CreateSessionAsync();
        var response = await fixture.AgentProxy.RunAsync("Hello", session);

        Assert.Contains("Echo", response.Text);
    }

    [Fact]
    public async Task MultiTurn_PreservesConversationHistory()
    {
        var session = await fixture.AgentProxy.CreateSessionAsync();

        var r1 = await fixture.AgentProxy.RunAsync("First message", session);
        var r2 = await fixture.AgentProxy.RunAsync("Second message", session);

        // Turn count increments as history accumulates
        Assert.Contains("Echo [1]", r1.Text);
        Assert.Contains("Echo [2]", r2.Text);
    }
}
```

### HITL Approval Flow Tests

The repository's [HITL approval integration tests](../../../tests/TemporalCommunity.Extensions.Agents.IntegrationTests/HITLApprovalTimeoutTests.cs)
cover timeout and resolution against a local Temporal service. The timeout case asserts
`ExpirationReason.Timeout`; the accepted case starts the approval update, resolves it with the
request ID, and asserts `DurableApprovalResolutionStatus.Accepted`. These are repository test
patterns, not a package-provided test fixture. For application-owned server setup, use the
[SDK-based local test environment](./dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests).

### Continue-as-New Tests

Repository integration tests verify conversation history and idle-deadline behavior across
continue-as-new, custom activity timeout propagation, and run-ID changes. They are not package
helpers. See
[ContinueAsNewTests.cs](../../../tests/TemporalCommunity.Extensions.Agents.IntegrationTests/ContinueAsNewTests.cs)
for complete setup and assertions. In particular,
`ContinueAsNew_RunIdChangesAfterSufficientHistory` captures the run ID before sending turns and
polls `DescribeAsync` with a bounded timeout until it changes; the history-preservation test also
checks the conversation after continue-as-new. `ContinueAsNew_RenewedIdleDeadlineSurvivesNewRunAndReplay`
and `ContinueAsNew_CustomTimeoutsPropagate` cover the idle-deadline and timeout behavior. The tests
lower the local service's event-history threshold through a repository-only helper.
Application-owned tests can pass the corresponding dynamic-config arguments through
`DevServerOptions.ExtraArgs` in the [SDK setup](./dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests).

### Custom Host Per Test

When a test needs isolated configuration, build another host using the fixture's client and a
distinct task queue. The repository's `AgentFactory_ResolvesServiceDependencies_AtActivityTime`
test defines its own `IGreetingService`, `GreetingService`, and `GreetingChatClient` test doubles
to prove a chat-client factory resolves its dependency during activity execution; those are not
package APIs. The complete test and helper definitions are in
[AgentIntegrationTests.cs](../../../tests/TemporalCommunity.Extensions.Agents.IntegrationTests/AgentIntegrationTests.cs):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

public sealed class CustomHostPerTest(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task AgentFactory_ResolvesServiceDependencies_AtActivityTime()
    {
        var taskQueue = $"factory-di-{Guid.NewGuid():N}";
        var greeting = "Greetings from DI!";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(fixture.Client);
        builder.Services.AddSingleton<IGreetingService>(new GreetingService(greeting));

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("DIAgent", agent =>
                {
                    agent.ChatClient = sp =>
                    {
                        var service = sp.GetRequiredService<IGreetingService>();
                        return new GreetingChatClient(service);
                    };
                });
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DIAgent");
            var session = await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Hello", session);

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains(greeting, response.Messages[0].Text);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
```

---

## Running Tests

The `just` recipes below run this repository's test projects; package consumers should use their
own test project and the SDK-based local environment linked above.

```bash
# Unit tests only (fast, no server needed)
just test-unit

# Integration tests (local Temporal server)
just test-integration

# Both suites
just test

# Filter by test name
dotnet test tests/TemporalCommunity.Extensions.Agents.Tests \
  --filter "FullyQualifiedName~StateBag"

# All tests with code coverage
just coverage-report
```

Repository maintainers can measure the failed-turn StateBag snapshot restoration without a Temporal server or model:

```bash
just benchmark-statebag
```

This repository-only BenchmarkDotNet benchmark sets up empty, approximately 64 KB, and
approximately 1 MB snapshots split across one or 64 top-level keys, then measures restoring the
pre-turn `JsonElement` snapshot by assignment. It does not measure StateBag serialization or a
deep copy. Results include timing and managed allocations and are informational, not cross-machine
thresholds.

The unit suite replays the checked-in `AgentWorkflow` histories without a running server. This
checks those fixtures only; it is not a general historical-workflow compatibility guarantee.
`just capture-histories` runs both libraries' capture tests to regenerate their fixtures after an
intentional workflow-command change:

```bash
just capture-histories
```

> **Run the integration suite with the history-capture filter.** Capture tests tagged
> `Category=HistoryCapture` overwrite checked-in fixtures with `File.WriteAllTextAsync`; the public
> `just test` and `just test-integration` recipes and diagnostic helpers `_test-individual` and
> `_test-logged` exclude them with `--filter "Category!=HistoryCapture"`. The explicit
> `capture-histories` recipe is the intentional exception and runs both libraries' capture tests,
> regenerating replay fixtures for MEAI and MAF.
> A bare `dotnet test tests/TemporalCommunity.Extensions.Agents.IntegrationTests` carries no filter,
> so it includes capture tests and may rewrite checked-in history fixtures. Prefer
> `just test-integration` for ordinary runs. After an unfiltered run, inspect
> `git diff -- tests/TemporalCommunity.Extensions.Agents.IntegrationTests/Compat/Histories` and
> preserve intentional changes; if discarding generated output, restore only individually reviewed
> fixture paths. The explicit `capture-histories` recipe also regenerates MEAI fixtures.
>
> Any new ordinary test recipe that runs `dotnet test` against an integration project must carry
> that exclusion — only the explicit history-capture recipes intentionally select the category.

> Repository integration tests use the test-project-only `TestEnvironmentHelper.StartLocalAsync()`;
> no external server is required. It wraps `TemporalServiceTestEnvironment`, pins Temporal CLI
> `v1.8.0` (embedded Temporal Service 1.31.2), verifies the reported version through
> `GetSystemInfo`, pre-registers the `AgentName`, `SessionCreatedAt`, and `TurnCount` search
> attributes, and configures `TemporalAgentDataConverter`. For application-owned tests, use the
> SDK-based setup in [Configure a local Temporal server](./dos-and-donts.md#configure-a-local-temporal-server-for-agents-integration-tests).

---

## References

- `tests/TemporalCommunity.Extensions.Agents.Tests/` — unit tests and server-free history replay
- `tests/TemporalCommunity.Extensions.Agents.IntegrationTests/` — embedded-server integration tests
- [Durability & Determinism](../../architecture/MAF/durability-and-determinism.md) — why activity results are cached on replay
- [Agent Sessions & Workflow Loop](../../architecture/MAF/agent-sessions-and-workflow-loop.md) — session lifecycle under test

---
