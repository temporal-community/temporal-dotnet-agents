using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.IntegrationTests.Helpers;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Tests.StepMode;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// S-T1-2 — session-scope StateBag bounding. Session-scope grants must:
/// <list type="bullet">
/// <item><b>Dedup by (ToolName, Pattern):</b> re-granting the same tool/pattern replaces the prior
/// record (latest <c>GrantedAt</c> wins) rather than appending — so the same tool keeps
/// auto-approving and the record count does not grow.</item>
/// <item><b>Bound by the always-scope budget:</b> when a new (distinct) grant would push the
/// deduplicated session set past <c>MaxAlwaysScopeCacheRecords</c>, the grant is REJECTED, degraded
/// to this-call-only, and a warning fires — so on a later turn that tool re-prompts while
/// already-persisted tools keep auto-approving.</item>
/// </list>
///
/// <para>
/// CAN is irrelevant here — these are single-session, multi-turn workflows. The overflow warning is
/// captured via a <see cref="CapturingLoggerProvider"/>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class SessionScopeBoundingTests : IClassFixture<SessionScopeBoundingTests.Fixture>
{
    private readonly Fixture _fixture;
    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment Env => _fixture.Environment;

    public SessionScopeBoundingTests(Fixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Dedup: re-granting the same tool keeps auto-approving (count stays 1) ────

    [Fact]
    public async Task SessionScope_RegrantSameTool_DedupKeepsAutoApprove_UnderTightBudget()
    {
        const string toolName = "write_file";

        // 3 turns of: tool call → final. Turn 1 grants Session; turns 2 & 3 should auto-approve.
        var scripted = ToolThenFinalScript(toolName, turns: 3);

        var capture = new CapturingLoggerProvider();
        // Budget of exactly 1 record: dedup must keep the single (write_file, null) record at 1,
        // so re-grants never overflow and the tool keeps auto-approving.
        using var host = BuildSingleToolHost("DedupAgent", scripted, toolName, capture, maxRecords: 1);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DedupAgent");
            var session = await proxy.CreateSessionAsync();
            var handle = Env.Client.GetWorkflowHandle<AgentWorkflow>(
                ((TemporalAgentSession)session).SessionId.WorkflowId);

            // Turn 1: park → approve Session.
            var t1 = proxy.RunAsync("write 1", session);
            var p1 = await WaitForPendingAsync(handle);
            Assert.NotNull(p1);
            await ApproveAsync(handle, p1!, ApprovalScope.Session);
            await t1;

            // Turn 2: same tool → auto-approve (no pending).
            var t2 = proxy.RunAsync("write 2", session);
            await t2;
            Assert.Null(await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(wf => wf.GetPendingApproval()));

            // Turn 3: still auto-approves — dedup kept the record count within the budget of 1.
            var t3 = proxy.RunAsync("write 3", session);
            await t3;
            Assert.Null(await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(wf => wf.GetPendingApproval()));

            // No budget-overflow warning — dedup prevented growth.
            Assert.False(capture.ContainsLog(LogLevel.Warning, "session-scope budget"),
                "Re-granting the same tool/pattern must dedup, not overflow.");

            await host.StopAsync();
        }
        catch
        {
            await host.StopAsync();
            throw;
        }
    }

    // ── Budget overflow: distinct grant rejected + degraded + warning ───────────

    [Fact]
    public async Task SessionScope_DistinctGrantOverBudget_RejectedAndDegraded()
    {
        const string toolA = "tool_a";
        const string toolB = "tool_b";

        // Turn 1: tool_a (grant Session, fits budget=1).
        // Turn 2: tool_b (grant Session, would be record #2 → overflow → rejected/degraded).
        // Turn 3: tool_a auto-approves; tool_b re-prompts (its grant never persisted).
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("a1", toolA, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a done 1")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("b1", toolB, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "b done 1")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("a2", toolA, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a done 2")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("b2", toolB, new Dictionary<string, object?>())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "b done 2")),
        ]);

        var capture = new CapturingLoggerProvider();
        using var host = BuildTwoToolHost("OverflowAgent", scripted, toolA, toolB, capture, maxRecords: 1);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("OverflowAgent");
            var session = await proxy.CreateSessionAsync();
            var handle = Env.Client.GetWorkflowHandle<AgentWorkflow>(
                ((TemporalAgentSession)session).SessionId.WorkflowId);

            // Turn 1: tool_a grant Session (fits).
            var t1 = proxy.RunAsync("a1", session);
            var p1 = await WaitForPendingAsync(handle);
            Assert.NotNull(p1);
            Assert.Equal(toolA, p1!.FunctionName);
            await ApproveAsync(handle, p1, ApprovalScope.Session);
            await t1;

            // Turn 2: tool_b grant Session → overflow → degraded to this-call-only + warning.
            var t2 = proxy.RunAsync("b1", session);
            var p2 = await WaitForPendingAsync(handle);
            Assert.NotNull(p2);
            Assert.Equal(toolB, p2!.FunctionName);
            await ApproveAsync(handle, p2, ApprovalScope.Session);
            await t2;

            Assert.True(capture.ContainsLog(LogLevel.Warning, "Session approval grant rejected"),
                "Expected a session-scope budget-overflow warning for the distinct grant.");

            // Turn 3: tool_a still auto-approves (record persisted), tool_b re-prompts (rejected).
            var t3 = proxy.RunAsync("a2", session);
            await t3; // tool_a auto-approves — no pending for it
            var t4 = proxy.RunAsync("b2", session);
            var p4 = await WaitForPendingAsync(handle);
            Assert.NotNull(p4); // tool_b must re-prompt — its grant degraded to this-call-only
            Assert.Equal(toolB, p4!.FunctionName);
            await ApproveAsync(handle, p4, ApprovalScope.ThisCallOnly);
            await t4;

            await host.StopAsync();
        }
        catch
        {
            await host.StopAsync();
            throw;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ScriptedChatClient ToolThenFinalScript(string toolName, int turns)
    {
        var responses = new List<ChatResponse>();
        for (var i = 0; i < turns; i++)
        {
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"{toolName}-{i}", toolName, new Dictionary<string, object?>())])));
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"done {i}")));
        }
        return new ScriptedChatClient(responses);
    }

    private static async Task<DurableApprovalRequest?> WaitForPendingAsync(WorkflowHandle<AgentWorkflow> handle)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(wf => wf.GetPendingApproval());
            if (pending is not null) return pending;
        }
        return null;
    }

    private static async Task ApproveAsync(
        WorkflowHandle<AgentWorkflow> handle,
        DurableApprovalRequest req,
        ApprovalScope scope)
    {
        if (scope == ApprovalScope.Session)
        {
            _ = await handle.ExecuteUpdateAsync(wf => wf.GrantSessionApprovalScopeAsync(
                new SessionApprovalScopeGrantRequest
                {
                    RequestId = req.RequestId,
                    MatchAllArguments = true,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                }));
            return;
        }

        _ = await handle.ExecuteUpdateAsync(wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
        {
            RequestId = req.RequestId,
            Approved = true,
        }));
    }

    private IHost BuildSingleToolHost(
        string agentName, IChatClient client, string toolName,
        CapturingLoggerProvider capture, int maxRecords)
    {
        var tool = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = toolName });
        return BuildHost(agentName, client, capture, maxRecords, agent =>
            agent.AddTool(tool, o => o.RequireApproval().ScopeAware()));
    }

    private IHost BuildTwoToolHost(
        string agentName, IChatClient client, string toolA, string toolB,
        CapturingLoggerProvider capture, int maxRecords)
    {
        var a = AIFunctionFactory.Create(() => "a", new AIFunctionFactoryOptions { Name = toolA });
        var b = AIFunctionFactory.Create(() => "b", new AIFunctionFactoryOptions { Name = toolB });
        return BuildHost(agentName, client, capture, maxRecords, agent =>
        {
            agent.AddTool(a, o => o.RequireApproval().ScopeAware());
            agent.AddTool(b, o => o.RequireApproval().ScopeAware());
        });
    }

    private IHost BuildHost(
        string agentName, IChatClient client, CapturingLoggerProvider capture,
        int maxRecords, Action<DurableAgentBuilder> configureTools)
    {
        var taskQueue = $"session-scope-bound-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Env.Client);
        builder.Services.AddSingleton(client);
        builder.Services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(capture);
        });

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent(agentName, agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureTools(agent);
                    // Tight budget so a second distinct session grant overflows.
                    agent.UseApprovalScopes(o => o.MaxSessionScopeRecords = maxRecords);
                });
            });

        return builder.Build();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public WorkflowEnvironment Environment { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Environment = await TestEnvironmentHelper.StartLocalAsync();
            Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        }

        public Task DisposeAsync() => Environment.ShutdownAsync();
    }
}
