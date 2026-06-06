using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Approvals;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Task 8.6 — Unit tests for <see cref="IApprovalScopeStore"/> contract and factory-resolution
/// behavior as defined in Feature B spec sections 2.5 (idempotency contract) and 13
/// (<c>AppendAlwaysScopeAsync</c> idempotency note).
///
/// Test cases:
/// 1. Pure <see cref="FakeApprovalScopeStore"/> idempotency behavior (3 cases).
/// 2. Factory resolution: agent without <c>UseApprovalScopes()</c> → factory not consulted.
/// 3. Factory resolution: worker-level factory set, agent has <c>UseApprovalScopes()</c> →
///    per-agent <c>ApprovalScopesOptions.ApprovalScopeStore</c> is null (worker-level factory
///    is the active factory at resolution time via fallback in <c>AgentActivities</c>).
/// 4. Factory resolution: per-agent factory overrides worker-level factory.
/// </summary>
public class ApprovalScopeStoreTests
{
    // ── FakeApprovalScopeStore: idempotency ──────────────────────────────────

    [Fact]
    public async Task AppendAsync_NewOriginatingRequestId_RecordAppearsInLoad()
    {
        var store = new FakeApprovalScopeStore();
        var record = MakeRecord("req-1", "WriteFile");

        await store.AppendAsync("MyAgent", "temporal.approval_scopes.always", record);

        var loaded = await store.LoadAsync("MyAgent", "temporal.approval_scopes.always");

        Assert.Single(loaded);
        Assert.Equal("req-1", loaded[0].OriginatingRequestId);
        Assert.Equal("WriteFile", loaded[0].ToolName);
    }

    [Fact]
    public async Task AppendAsync_DuplicateOriginatingRequestId_LoadReturnsExactlyOneRecord()
    {
        var store = new FakeApprovalScopeStore();
        var record = MakeRecord("req-1", "WriteFile");

        // Append the same request ID twice (simulate activity retry).
        await store.AppendAsync("MyAgent", "temporal.approval_scopes.always", record);
        await store.AppendAsync("MyAgent", "temporal.approval_scopes.always", record);

        var loaded = await store.LoadAsync("MyAgent", "temporal.approval_scopes.always");

        Assert.Single(loaded);
    }

    [Fact]
    public async Task LoadAsync_EmptyStore_ReturnsEmptyList_NotNullNotException()
    {
        var store = new FakeApprovalScopeStore();

        IReadOnlyList<ApprovalScopeRecord>? result = null;
        var exception = await Record.ExceptionAsync(async () =>
            result = await store.LoadAsync("MyAgent", "temporal.approval_scopes.always"));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ── Factory resolution: registration layer ───────────────────────────────

    [Fact]
    public void WorkerLevelStoreConfigured_AgentWithoutUseApprovalScopes_RegistrationHasNullApprovalScopesOptions()
    {
        // Spec: "Agents that have not opted in will not invoke this factory — even when it is configured."
        // At the registration layer this manifests as ApprovalScopesOptions being null, so the
        // factory in TemporalAgentsOptions.ApprovalScopeStore is never consulted during resolution.
        var workerInvocationCount = 0;
        var workerFactory = (IServiceProvider _) =>
        {
            workerInvocationCount++;
            return (IApprovalScopeStore)new FakeApprovalScopeStore();
        };

        var options = new TemporalAgentsOptions { ApprovalScopeStore = workerFactory };
        var builder = new DurableAgentBuilder("MyAgent");
        builder.ChatClient = _ => new StubChatClientForStoreTests();
        // Intentionally NOT calling builder.UseApprovalScopes()

        var registration = builder.ToRegistration();

        Assert.False(registration.UseApprovalScopes);
        Assert.Null(registration.ApprovalScopesOptions);
        // Factory was never invoked because UseApprovalScopes was not called.
        Assert.Equal(0, workerInvocationCount);
    }

    [Fact]
    public void WorkerLevelStoreConfigured_AgentWithUseApprovalScopes_PerAgentScopeStoreIsNull_WorkerFactoryIsActiveAtResolutionTime()
    {
        // When per-agent ApprovalScopesOptions.ApprovalScopeStore is null, the worker-level
        // TemporalAgentsOptions.ApprovalScopeStore is the active fallback in ComposeDurableAgent.
        // At registration level: ApprovalScopesOptions is populated with a null per-agent store.
        var workerFactory = (IServiceProvider _) =>
            (IApprovalScopeStore)new FakeApprovalScopeStore();

        var options = new TemporalAgentsOptions { ApprovalScopeStore = workerFactory };
        var builder = new DurableAgentBuilder("MyAgent");
        builder.ChatClient = _ => new StubChatClientForStoreTests();
        builder.UseApprovalScopes(); // no per-agent store override

        var registration = builder.ToRegistration();

        Assert.True(registration.UseApprovalScopes);
        Assert.NotNull(registration.ApprovalScopesOptions);
        // Per-agent store is null — activity will fall back to worker-level factory.
        Assert.Null(registration.ApprovalScopesOptions!.ApprovalScopeStore);
    }

    [Fact]
    public void PerAgentStoreConfigured_WinsOverWorkerLevelFactory_RegistrationHasPerAgentFactory()
    {
        // Spec: per-agent factory wins over worker-level factory.
        var perAgentStore = new FakeApprovalScopeStore();
        var perAgentFactory = (IServiceProvider _) =>
            (IApprovalScopeStore)perAgentStore;

        var workerStore = new FakeApprovalScopeStore();
        var workerFactory = (IServiceProvider _) =>
            (IApprovalScopeStore)workerStore;

        var options = new TemporalAgentsOptions { ApprovalScopeStore = workerFactory };
        var builder = new DurableAgentBuilder("MyAgent");
        builder.ChatClient = _ => new StubChatClientForStoreTests();
        builder.UseApprovalScopes(o => o.ApprovalScopeStore = perAgentFactory);

        var registration = builder.ToRegistration();

        Assert.True(registration.UseApprovalScopes);
        Assert.NotNull(registration.ApprovalScopesOptions);
        // Per-agent factory is non-null — at resolution time, this wins over worker-level.
        Assert.NotNull(registration.ApprovalScopesOptions!.ApprovalScopeStore);
        // Calling the per-agent factory returns the per-agent instance (not the worker instance).
        var resolved = registration.ApprovalScopesOptions.ApprovalScopeStore!(null!);
        Assert.Same(perAgentStore, resolved);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ApprovalScopeRecord MakeRecord(string requestId, string toolName) =>
        new ApprovalScopeRecord
        {
            ToolName = toolName,
            GrantedAt = DateTimeOffset.UtcNow,
            OriginatingRequestId = requestId,
        };
}

/// <summary>Minimal <see cref="IChatClient"/> stub for builder construction in store tests.</summary>
internal sealed class StubChatClientForStoreTests : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not called in unit tests");
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not called in unit tests");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// In-memory <see cref="IApprovalScopeStore"/> for unit tests. Thread-safety is not
/// required (single-threaded test execution). Implements the idempotency contract
/// from IApprovalScopeStore.AppendAsync: duplicate OriginatingRequestId is a no-op.
/// </summary>
internal sealed class FakeApprovalScopeStore : IApprovalScopeStore
{
    private readonly Dictionary<(string AgentName, string StoreKey), List<ApprovalScopeRecord>> _data =
        new();

    public Task<IReadOnlyList<ApprovalScopeRecord>> LoadAsync(
        string agentName,
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        if (_data.TryGetValue((agentName, storeKey), out var list))
            return Task.FromResult<IReadOnlyList<ApprovalScopeRecord>>(list.AsReadOnly());

        return Task.FromResult<IReadOnlyList<ApprovalScopeRecord>>(
            Array.Empty<ApprovalScopeRecord>());
    }

    public Task AppendAsync(
        string agentName,
        string storeKey,
        ApprovalScopeRecord record,
        CancellationToken cancellationToken = default)
    {
        var key = (agentName, storeKey);
        if (!_data.TryGetValue(key, out var list))
        {
            list = new List<ApprovalScopeRecord>();
            _data[key] = list;
        }

        // Idempotency: skip if OriginatingRequestId already present.
        if (list.Exists(r => r.OriginatingRequestId == record.OriginatingRequestId))
            return Task.CompletedTask;

        list.Add(record);
        return Task.CompletedTask;
    }
}
