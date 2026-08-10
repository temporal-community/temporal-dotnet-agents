using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tasks 8.7 and 8.8 — Workflow-level tests for Feature B (Approval Scopes).
///
/// These tests require a real embedded Temporal server (WorkflowEnvironment) because:
/// - Session-scope StateBag writes happen inside AgentWorkflow.RunAsync — no mock can replace
///   this without replaying actual Temporal history.
/// - CAN survival requires the actual Workflow.CreateContinueAsNewException path.
/// - AppendAlwaysScopeAsync dispatch must appear in Temporal workflow history.
///
/// Tests placed in the integration test project rather than the unit test project because
/// just test-unit-all runs without a server.
/// </summary>
[Trait("Category", "Integration")]
public class ApprovalScopeWorkflowTests : IClassFixture<ApprovalScopeEnvironmentFixture>
{
    private readonly ApprovalScopeEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment _env => _fixture.Environment;

    private const string LoadAlwaysScopesActivity = "TemporalCommunity.Extensions.Agents.LoadAlwaysScopes";
    private const string AppendAlwaysScopeActivity = "TemporalCommunity.Extensions.Agents.AppendAlwaysScope";
    private const string RunToolInterceptorActivity = "TemporalCommunity.Extensions.Agents.RunToolInterceptor";

    public ApprovalScopeWorkflowTests(ApprovalScopeEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Task 8.7.1 — Session scope written, second call auto-approves ────────

    /// <summary>
    /// Verifies the session-scope write path (spec Section 4):
    /// Turn 1: scope-aware required tool → interceptor fires PauseForApproval.
    ///   → Test submits approval with Scope = Session.
    ///   → Workflow writes ApprovalScopeRecord to StateBag key "temporal.approval_scopes.session".
    /// Turn 2: same tool call → interceptor finds matching session scope → returns Proceed.
    ///   → No new approval resolution needed.
    /// </summary>
    [Fact]
    public async Task SessionScope_WrittenAfterApproval_SecondCallAutoApproves()
    {
        var toolCallId = Guid.NewGuid().ToString("N")[..8];
        var toolName = "write_file";

        // Turn 1: tool call → final answer. Turn 2: tool call → final answer (auto-approved).
        var scriptedClient = new ScriptedChatClient([
            // Turn 1 — first RunAsync call
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(toolCallId, toolName, new Dictionary<string, object?> { ["path"] = "/tmp/hello.txt" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "File written.")),
            // Turn 2 — second RunAsync call (scope record already in StateBag)
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(toolCallId + "2", toolName, new Dictionary<string, object?> { ["path"] = "/tmp/hello.txt" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "File written again.")),
        ]);

        using var host = BuildScopeAwareHost(scriptedClient, toolName);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("ScopeAgent");
        var session = await proxy.CreateSessionAsync();
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        // Turn 1: run the agent — workflow will pause for approval.
        var runTask = proxy.RunAsync("Write /tmp/hello.txt", session);

        // Wait for the workflow to register a pending approval.
        DurableApprovalRequest? pendingApproval = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            pendingApproval = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (pendingApproval is not null) break;
        }

        Assert.NotNull(pendingApproval);
        _output.WriteLine($"Pending approval: {pendingApproval!.RequestId}");

        // Submit approval with Scope = Session.
        await handle.ExecuteUpdateAsync(wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
        {
            RequestId = pendingApproval!.RequestId,
            Approved = true,
            Scope = ApprovalScope.Session,
        }));

        var turn1Response = await runTask;
        Assert.NotNull(turn1Response);
        _output.WriteLine($"Turn 1 response: {turn1Response.Messages[^1].Text}");

        // Turn 2: run the agent again with the same tool call.
        // The scope record written in Turn 1 should allow the interceptor to auto-approve.
        var turn2Response = await proxy.RunAsync("Write /tmp/hello.txt again", session);
        Assert.NotNull(turn2Response);
        _output.WriteLine($"Turn 2 response: {turn2Response.Messages[^1].Text}");

        // Verify: no pending approval after Turn 2 (auto-approved via scope).
        var pendingAfterTurn2 = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval());
        Assert.Null(pendingAfterTurn2);

        // Verify: RunToolInterceptor activity appears in history (interceptor was invoked both turns).
        var activityNames = await CollectActivityNamesAsync(handle);
        var interceptorCount = activityNames.Count(n => n == RunToolInterceptorActivity);
        Assert.True(interceptorCount >= 1,
            $"Expected at least 1 RunToolInterceptor activity; found {interceptorCount}");

        await host.StopAsync();
    }

    [Fact]
    public async Task SessionScope_ResolvedWhileTurnIsParked_SurvivesTurnFailure()
    {
        const string toolName = "unstable_write_file";
        const string path = "/tmp/approval-survives-failure.txt";
        var scriptedClient = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("failed-call", toolName,
                    new Dictionary<string, object?> { ["path"] = path })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("recovery-call", toolName,
                    new Dictionary<string, object?> { ["path"] = path })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered without another approval.")),
        ]);
        var toolCallCount = 0;

        using var host = BuildFailOnceScopeAwareHost(
            scriptedClient,
            toolName,
            () => Interlocked.Increment(ref toolCallCount));
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailedTurnScopeAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);

            var failedTurn = proxy.RunAsync("Write the file", session);
            var pending = await WaitForPendingApprovalAsync(handle);

            await handle.ExecuteUpdateAsync(wf => wf.ResolveAgentApprovalAsync(
                new DurableAgentApprovalDecision
                {
                    RequestId = pending.RequestId,
                    Approved = true,
                    Scope = ApprovalScope.Session,
                }));

            await Assert.ThrowsAnyAsync<Exception>(() => failedTurn);

            // The first tool invocation failed after the independent approval Update committed
            // its session-scope record. The next turn must consume that record rather than park
            // for a second approval.
            var recovered = await proxy.RunAsync("Try the same write again", session)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("Recovered without another approval.", recovered.Messages[^1].Text);
            Assert.Equal(2, toolCallCount);
            Assert.Null(await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval()));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Task 8.7.4 — Non-scope-aware tool with Session scope → LogWarning only ─

    /// <summary>
    /// Verifies that approving a non-scope-aware tool with Scope = Session:
    /// - Does NOT throw or crash the workflow.
    /// - The tool proceeds normally (ThisCallOnly semantics).
    /// - Scope record is NOT persisted (no auto-approval on subsequent calls).
    /// </summary>
    [Fact]
    public async Task NonScopeAwareTool_SessionScopeRequested_WorkflowProceedsNormally()
    {
        var toolName = "plain_tool";
        var toolCallId = Guid.NewGuid().ToString("N")[..8];

        var scriptedClient = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(toolCallId, toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Plain tool ran.")),
            // Second turn — same tool → must request approval again (no scope was persisted).
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(toolCallId + "2", toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Plain tool ran again.")),
        ]);

        using var host = BuildNonScopeAwareRequiredHost(scriptedClient, toolName);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("PlainRequiredAgent");
        var session = await proxy.CreateSessionAsync();
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        // Turn 1: run — pause for approval.
        var runTask = proxy.RunAsync("Run plain tool", session);

        DurableApprovalRequest? pending = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (pending is not null) break;
        }
        Assert.NotNull(pending);

        // Approve with Scope = Session (non-scope-aware tool ignores the scope).
        await handle.ExecuteUpdateAsync(wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
        {
            RequestId = pending!.RequestId,
            Approved = true,
            Scope = ApprovalScope.Session, // should be ignored + LogWarning
        }));

        var turn1Response = await runTask;
        Assert.NotNull(turn1Response);

        // Turn 2: same plain tool call → must block again (scope NOT persisted for non-scope-aware tool).
        var turn2Task = proxy.RunAsync("Run plain tool again", session);

        DurableApprovalRequest? pending2 = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            pending2 = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (pending2 is not null) break;
        }

        // The second call must ALSO request approval (no scope record was written in Turn 1).
        Assert.NotNull(pending2);

        // Approve Turn 2 so test cleanup succeeds.
        await handle.ExecuteUpdateAsync(wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
        {
            RequestId = pending2!.RequestId,
            Approved = true,
        }));
        await turn2Task;

        await host.StopAsync();
    }

    // ── Task 8.8 — Always-scope store: AppendAlwaysScopeAsync dispatched ─────

    /// <summary>
    /// Verifies that approving a scope-aware required tool with Scope = Always dispatches
    /// the AppendAlwaysScopeAsync activity in workflow history (spec Section 4).
    /// </summary>
    [Fact]
    public async Task AlwaysScope_WithStore_AppendAlwaysScopeAsyncActivityDispatched()
    {
        var toolName = "delete_file";
        var toolCallId = Guid.NewGuid().ToString("N")[..8];

        var scriptedClient = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(toolCallId, toolName, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "File deleted.")),
        ]);

        var fakeStore = new InMemoryApprovalScopeStore();

        using var host = BuildScopeAwareWithStoreHost(scriptedClient, toolName, fakeStore);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("AlwaysScopeAgent");
        var session = await proxy.CreateSessionAsync();
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        var runTask = proxy.RunAsync("Delete file", session);

        DurableApprovalRequest? pending = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (pending is not null) break;
        }
        Assert.NotNull(pending);
        _output.WriteLine($"Pending approval: {pending!.RequestId}");

        // Approve with Scope = Always.
        await handle.ExecuteUpdateAsync(wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
        {
            RequestId = pending!.RequestId,
            Approved = true,
            Scope = ApprovalScope.Always,
        }));

        var response = await runTask;
        Assert.NotNull(response);

        // Verify AppendAlwaysScopeAsync appears in workflow history.
        var activityNames = await CollectActivityNamesAsync(handle);
        Assert.Contains(AppendAlwaysScopeActivity, activityNames);

        // Verify the record was persisted in the fake store.
        var storeRecords = await fakeStore.LoadAsync("AlwaysScopeAgent", "temporal.approval_scopes.always");
        Assert.Single(storeRecords);
        Assert.Equal("delete_file", storeRecords[0].ToolName);

        await host.StopAsync();
    }

    // ── Task 8.8 — Always-scope store: LoadAlwaysScopesAsync at session start ─

    /// <summary>
    /// Verifies that LoadAlwaysScopesAsync is dispatched at session start when
    /// UseApprovalScopeStoreMode is true (store configured + UseApprovalScopes enabled).
    /// </summary>
    [Fact]
    public async Task AlwaysScope_WithStore_LoadAlwaysScopesActivityDispatchedAtStart()
    {
        var scriptedClient = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        ]);

        var fakeStore = new InMemoryApprovalScopeStore();
        // Pre-populate the store with one always-scope record.
        await fakeStore.AppendAsync("AlwaysScopeAgent2", "temporal.approval_scopes.always",
            new ApprovalScopeRecord
            {
                ToolName = "delete_file",
                GrantedAt = DateTimeOffset.UtcNow,
                OriginatingRequestId = Guid.NewGuid().ToString("N"),
            });

        using var host = BuildScopeAwareWithStoreHost2(scriptedClient, "delete_file", fakeStore);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("AlwaysScopeAgent2");
        var session = await proxy.CreateSessionAsync();
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        await proxy.RunAsync("Hello", session);

        var activityNames = await CollectActivityNamesAsync(handle);
        Assert.Contains(LoadAlwaysScopesActivity, activityNames);

        await host.StopAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<string>> CollectActivityNamesAsync(WorkflowHandle handle)
    {
        var names = new List<string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
                names.Add(a.ActivityType.Name);
        }
        return names;
    }

    private static async Task<DurableApprovalRequest> WaitForPendingApprovalAsync(
        WorkflowHandle<AgentWorkflow> handle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                    wf => wf.GetPendingApproval());
                if (pending is not null)
                    return pending;
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                // RunAsync starts the workflow asynchronously. Under the full suite's worker
                // load, the first query can reach Temporal before that start is visible.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("Timed out waiting for the agent approval request.");
    }

    /// <summary>
    /// Builds a host with a scope-aware required tool that uses UseApprovalScopes()
    /// but no IApprovalScopeStore (no always-scope persistence).
    /// </summary>
    private IHost BuildScopeAwareHost(IChatClient client, string toolName)
    {
        var taskQueue = $"scope-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton(client);

        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Path to write.")] string path) => $"Wrote {path}",
            new AIFunctionFactoryOptions { Name = toolName });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("ScopeAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, o => o.RequireApproval().ScopeAware());
                    agent.UseApprovalScopes();
                });
            });

        return builder.Build();
    }

    private IHost BuildFailOnceScopeAwareHost(
        IChatClient client,
        string toolName,
        Func<int> nextToolCall)
    {
        var taskQueue = $"failed-turn-scope-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton(client);

        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Path to write.")] string path) =>
            {
                if (nextToolCall() == 1)
                    throw new InvalidOperationException("Expected first tool invocation failure.");
                return $"Wrote {path}";
            },
            new AIFunctionFactoryOptions { Name = toolName });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("FailedTurnScopeAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, options =>
                        options.NoRetry().RequireApproval().ScopeAware());
                    agent.UseApprovalScopes();
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Builds a host with a plain required tool (NOT scope-aware) and NO UseApprovalScopes.
    /// </summary>
    private IHost BuildNonScopeAwareRequiredHost(IChatClient client, string toolName)
    {
        var taskQueue = $"plain-required-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton(client);

        var tool = AIFunctionFactory.Create(
            () => "done",
            new AIFunctionFactoryOptions { Name = toolName });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("PlainRequiredAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, o => o.RequireApproval()); // NOT scope-aware
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Builds a host with a scope-aware required tool AND an IApprovalScopeStore configured.
    /// Used for always-scope append/load tests.
    /// </summary>
    private IHost BuildScopeAwareWithStoreHost(IChatClient client, string toolName, InMemoryApprovalScopeStore store)
    {
        var taskQueue = $"always-scope-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IApprovalScopeStore>(store);

        var tool = AIFunctionFactory.Create(
            () => "deleted",
            new AIFunctionFactoryOptions { Name = toolName });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.ApprovalScopeStore = sp => sp.GetRequiredService<IApprovalScopeStore>();
                opts.AddDurableAgent("AlwaysScopeAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, o => o.RequireApproval().ScopeAware());
                    agent.UseApprovalScopes();
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Builds a host for AlwaysScopeAgent2 — separate agent name to avoid collision.
    /// </summary>
    private IHost BuildScopeAwareWithStoreHost2(IChatClient client, string toolName, InMemoryApprovalScopeStore store)
    {
        var taskQueue = $"always-scope-agent2-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IApprovalScopeStore>(store);

        var tool = AIFunctionFactory.Create(
            () => "deleted",
            new AIFunctionFactoryOptions { Name = toolName });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.ApprovalScopeStore = sp => sp.GetRequiredService<IApprovalScopeStore>();
                opts.AddDurableAgent("AlwaysScopeAgent2", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, o => o.RequireApproval().ScopeAware());
                    agent.UseApprovalScopes();
                });
            });

        return builder.Build();
    }
}

/// <summary>Shared fixture for ApprovalScopeWorkflowTests.</summary>
public sealed class ApprovalScopeEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}

/// <summary>
/// Thread-safe in-memory <see cref="IApprovalScopeStore"/> for integration tests.
/// Implements the idempotency contract: duplicate OriginatingRequestId is a no-op.
/// </summary>
internal sealed class InMemoryApprovalScopeStore : IApprovalScopeStore
{
    private readonly Dictionary<(string AgentName, string StoreKey), List<ApprovalScopeRecord>> _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<ApprovalScopeRecord>> LoadAsync(
        string agentName, string storeKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_data.TryGetValue((agentName, storeKey), out var list))
                return list.ToArray();
            return Array.Empty<ApprovalScopeRecord>();
        }
        finally { _lock.Release(); }
    }

    public async Task AppendAsync(
        string agentName, string storeKey, ApprovalScopeRecord record,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var key = (agentName, storeKey);
            if (!_data.TryGetValue(key, out var list))
            {
                list = new List<ApprovalScopeRecord>();
                _data[key] = list;
            }
            if (!list.Exists(r => r.OriginatingRequestId == record.OriginatingRequestId))
                list.Add(record);
        }
        finally { _lock.Release(); }
    }
}
