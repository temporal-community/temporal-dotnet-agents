using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Xunit;
using Xunit.Abstractions;

namespace TemporalCommunity.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests that the HITL approval timeout feature works correctly end-to-end.
/// When a human never responds to an approval request, the workflow should
/// return a rejected <see cref="DurableApprovalDecision"/> after the configured timeout.
/// </summary>
[Trait("Category", "Integration")]
public class HITLApprovalTimeoutTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public HITLApprovalTimeoutTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RequestApproval_TimesOut_ReturnsRejectedTicket()
    {
        // Arrange: spin up a worker with a very short approval timeout (2 seconds).
        var taskQueue = $"hitl-timeout-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("HITLAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                options.DefaultApprovalTimeout = TimeSpan.FromSeconds(2);
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Start the agent workflow so it's running and can accept updates.
            var proxy = host.Services.GetTemporalAgentProxy("HITLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Hello", session);
            Assert.NotNull(response);

            // Act: send a RequestApproval update directly to the workflow.
            // This simulates what TemporalAgentContext.RequestApprovalAsync does from inside a tool.
            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            var approvalRequest = new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Delete all records — This should time out."
            };

            // This call blocks until the approval decision arrives or the timeout elapses.
            var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(approvalRequest));

            // Assert: the decision should be rejected with a timeout message.
            Assert.False(decision.Approved);
            Assert.Equal(approvalRequest.RequestId, decision.RequestId);
            Assert.NotNull(decision.Reason);
            Assert.Contains("timed out", decision.Reason, StringComparison.OrdinalIgnoreCase);

            _output.WriteLine($"Approval timed out as expected: {decision.Reason}");

            // Verify the pending approval was cleared.
            var pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            Assert.Null(pending);

            // Timeout decisions are retained in both approval ledgers. A typed retry with the
            // synthesized ThisCallOnly scope is idempotent; a changed scope conflicts.
            var timeoutRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
                {
                    RequestId = decision.RequestId,
                    Approved = false,
                    Reason = decision.Reason,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, timeoutRetry.Status);

            var timeoutConflict = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
                {
                    RequestId = decision.RequestId,
                    Approved = false,
                    Reason = decision.Reason,
                    Scope = ApprovalScope.Session,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, timeoutConflict.Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ResolveAgentApproval_RetryAndGenericCrossEndpoint_ReturnExpectedStatuses()
    {
        // Verify the happy path still works: approval submitted before timeout elapses.
        var taskQueue = $"hitl-approve-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.EnableSearchAttributes = false;
                options.AddDurableAgent("HITLApproveAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                options.DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("HITLApproveAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hello", session);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            var approvalRequest = new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Send email — Should be approved promptly."
            };

            // Start the approval update in the background (it will block until decision).
            var approvalTask = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(approvalRequest));

            // Wait briefly for the workflow to register the pending approval.
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Resolve through the MAF-specific endpoint with reusable scope semantics.
            var typedDecision = new DurableAgentApprovalDecision
            {
                RequestId = approvalRequest.RequestId,
                Approved = true,
                Reason = "Looks good!",
                Scope = ApprovalScope.Session,
            };
            var accepted = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(typedDecision));
            Assert.Equal(DurableApprovalResolutionStatus.Accepted, accepted.Status);

            var decision = await approvalTask;

            Assert.True(decision.Approved);
            Assert.Equal(approvalRequest.RequestId, decision.RequestId);
            Assert.Equal("Looks good!", decision.Reason);

            // The same typed decision is retry-safe, but changing MAF-only scope identity is a
            // conflict even though the generic core fields are unchanged.
            var typedRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(typedDecision));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, typedRetry.Status);

            var changedScope = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
                {
                    RequestId = approvalRequest.RequestId,
                    Approved = true,
                    Reason = "Looks good!",
                    Scope = ApprovalScope.ThisCallOnly,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, changedScope.Status);

            // Shared dashboards can retry the core decision and see the generic status without
            // being able to grant MAF reusable scope.
            var genericRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = approvalRequest.RequestId,
                    Approved = true,
                    Reason = "Looks good!",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, genericRetry.Status);

            // Resolve a second request through the generic endpoint first. A typed retry can
            // only be equivalent when it explicitly uses the synthesized ThisCallOnly scope.
            var genericFirstRequest = new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Generic dashboard decision.",
            };
            var genericFirstTask = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(genericFirstRequest));
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var genericAccepted = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = genericFirstRequest.RequestId,
                    Approved = true,
                    Reason = "Approved generically.",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Accepted, genericAccepted.Status);
            await genericFirstTask;

            var typedAfterGeneric = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
                {
                    RequestId = genericFirstRequest.RequestId,
                    Approved = true,
                    Reason = "Approved generically.",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, typedAfterGeneric.Status);

            var typedScopeAfterGeneric = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveAgentApprovalAsync(new DurableAgentApprovalDecision
                {
                    RequestId = genericFirstRequest.RequestId,
                    Approved = true,
                    Reason = "Approved generically.",
                    Scope = ApprovalScope.Session,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, typedScopeAfterGeneric.Status);

            _output.WriteLine("Typed and generic approval retry statuses verified.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
