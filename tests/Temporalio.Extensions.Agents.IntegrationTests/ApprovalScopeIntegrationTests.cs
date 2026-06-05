using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Task 8.9 — Integration test: one-turn lag for multi-approval in one turn.
///
/// Spec section 9 explains the "one-turn lag" invariant:
/// When two scope-aware tool calls appear in the same LLM turn (Phase 1 fan-out), both
/// interceptor activities are dispatched BEFORE any approval wait begins. Therefore, a scope
/// record written when Call A is approved during Phase 2 processing does NOT retroactively
/// satisfy Call B's already-open approval request in the same turn.
///
/// The scope IS visible starting from turn N+1, where a fresh Phase 1 snapshot is taken.
///
/// These tests require a real embedded Temporal server because:
/// - The Phase 1 snapshot freeze is enforced by actual workflow history order.
/// - Verifying that Call B still blocks after Call A is approved requires real WaitConditionAsync
///   semantics — mocks cannot replicate the BLOCK-4 invariant.
/// </summary>
[Trait("Category", "Integration")]
public class ApprovalScopeIntegrationTests : IClassFixture<ApprovalScopeIntegrationFixture>
{
    private readonly ApprovalScopeIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;
    private WorkflowEnvironment _env => _fixture.Environment;

    public ApprovalScopeIntegrationTests(ApprovalScopeIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Verifies the one-turn lag invariant (spec Section 9):
    ///
    /// Turn N:
    ///   - LLM returns two FunctionCallContent items for the same scope-aware required tool.
    ///   - Phase 1: both interceptor activities are dispatched before any approval wait.
    ///   - Phase 2: Call A is approved with Scope = Session.
    ///     → workflow writes Call A's scope record to StateBag.
    ///   - Phase 2: Call B still requires separate approval (scope record is NOT visible to
    ///     Call B's already-completed Phase 1 result — the snapshot was frozen before Call A's
    ///     approval resolved).
    ///
    /// Turn N+1:
    ///   - LLM returns one more FunctionCallContent for the same tool.
    ///   - Phase 1: interceptor sees Call A's scope record and returns Proceed immediately.
    ///   - No new SubmitApprovalAsync is needed.
    /// </summary>
    [Fact]
    public async Task OneTurnLag_CallB_RequiresOwnApproval_TurnNPlus1_AutoApproves()
    {
        var toolName = "write_file";
        var callAId = "call-a-" + Guid.NewGuid().ToString("N")[..6];
        var callBId = "call-b-" + Guid.NewGuid().ToString("N")[..6];
        var callCId = "call-c-" + Guid.NewGuid().ToString("N")[..6];

        // Turn N: two tool calls in the same LLM response (same tool name, distinct call IDs).
        // Turn N+1 final: LLM returns one more tool call (auto-approved by scope), then final answer.
        var scriptedClient = new ScriptedChatClient([
            // Turn N — LLM responds with two tool calls in one message.
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callAId, toolName, new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" }),
                new FunctionCallContent(callBId, toolName, new Dictionary<string, object?> { ["path"] = "/tmp/b.txt" }),
            ])),
            // Turn N — after both tools run, LLM gives summary.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Wrote both files.")),
            // Turn N+1 — one more tool call for the same tool (should be auto-approved via scope).
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callCId, toolName, new Dictionary<string, object?> { ["path"] = "/tmp/c.txt" }),
            ])),
            // Turn N+1 — final answer.
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "All done.")),
        ]);

        using var host = BuildScopeAwareHost(scriptedClient, toolName);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("OneTurnLagAgent");
        var session = await proxy.CreateSessionAsync();
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        // ── Turn N ──────────────────────────────────────────────────────────────

        // Start the agent. Both Call A and Call B will enter Phase 1 before any approval wait.
        var turnNTask = proxy.RunAsync("Write two files", session);

        // Wait for Call A's approval request (first pending approval in Phase 2 sequential loop).
        DurableApprovalRequest? callARequest = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            callARequest = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            if (callARequest is not null) break;
        }
        Assert.NotNull(callARequest);
        _output.WriteLine($"Turn N, Call A pending approval: {callARequest!.RequestId}");

        // Approve Call A with Scope = Session.
        // This writes the scope record to _currentStateBag, but Call B's interceptor result
        // was already frozen in Phase 1 — Call B cannot see this record in the same turn.
        await handle.ExecuteUpdateAsync(wf => wf.SubmitApprovalAsync(new DurableApprovalDecision
        {
            RequestId = callARequest!.RequestId,
            Approved = true,
            Scope = ApprovalScope.Session,
        }));
        _output.WriteLine("Turn N, Call A approved with Scope = Session.");

        // After Call A resolves, Phase 2 must now wait for Call B's separate approval.
        // Poll until Call B's pending request appears.
        DurableApprovalRequest? callBRequest = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            callBRequest = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            // Must be a different request (Call B), not Call A's closed request.
            if (callBRequest is not null && callBRequest.RequestId != callARequest.RequestId) break;
            callBRequest = null;
        }

        // ── Core assertion: one-turn lag ──────────────────────────────────────
        // Call B's Phase 1 interceptor ran before Call A's approval → it returned
        // PauseForApproval regardless of Call A's scope record. Phase 2 must now
        // be waiting for Call B's own approval.
        Assert.NotNull(callBRequest);
        Assert.NotEqual(callARequest.RequestId, callBRequest!.RequestId);
        _output.WriteLine($"Turn N, Call B still pending (one-turn lag confirmed): {callBRequest.RequestId}");

        // Approve Call B (plain approval, no scope).
        await handle.ExecuteUpdateAsync(wf => wf.SubmitApprovalAsync(new DurableApprovalDecision
        {
            RequestId = callBRequest!.RequestId,
            Approved = true,
        }));
        _output.WriteLine("Turn N, Call B approved.");

        var turnNResponse = await turnNTask;
        Assert.NotNull(turnNResponse);
        _output.WriteLine($"Turn N response: {turnNResponse.Messages[^1].Text}");

        // ── Turn N+1: scope record from Call A is now visible ─────────────────
        // A fresh Phase 1 snapshot includes the scope record written when Call A was approved.
        // Call C (same tool, new call ID) should be auto-approved without human interaction.
        var turnNPlus1Response = await proxy.RunAsync("Write one more file", session);
        Assert.NotNull(turnNPlus1Response);
        _output.WriteLine($"Turn N+1 response: {turnNPlus1Response.Messages[^1].Text}");

        // Verify no pending approval remains after Turn N+1 (auto-approved via scope).
        var pendingAfterTurnNPlus1 = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval());
        Assert.Null(pendingAfterTurnNPlus1);
        _output.WriteLine("Turn N+1: no pending approval — auto-approved via session scope.");

        await host.StopAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IHost BuildScopeAwareHost(IChatClient client, string toolName)
    {
        var taskQueue = $"one-turn-lag-agent-{Guid.NewGuid():N}";
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
                opts.AddDurableAgent("OneTurnLagAgent", agent =>
                {
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    agent.AddTool(tool, o => o.RequireApproval().ScopeAware());
                    agent.UseApprovalScopes();
                });
            });

        return builder.Build();
    }
}

/// <summary>Shared fixture for ApprovalScopeIntegrationTests.</summary>
public sealed class ApprovalScopeIntegrationFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}
