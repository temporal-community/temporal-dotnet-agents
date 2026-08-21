using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
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
    public async Task RequestApproval_EmptyRequestIdFailsTerminallyAndWorkflowRemainsHealthy()
    {
        var proxy = _fixture.AgentProxy;
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var first = await proxy.RunAsync("Establish approval workflow", session);
        Assert.Contains("Echo [1]:", first.Messages[0].Text);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
        var scheduledBefore = await CountScheduledActivitiesAsync(handle);
        var invalid = new DurableApprovalRequest
        {
            RequestId = string.Empty,
            Description = "Invalid request must not park the workflow.",
        };

        var updateFailure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                workflow => workflow.RequestApprovalAsync(invalid)));
        var applicationFailure = Assert.IsType<ApplicationFailureException>(updateFailure.InnerException);
        Assert.Equal("DurableApprovalInvalidRequest", applicationFailure.ErrorType);
        Assert.True(applicationFailure.NonRetryable);
        Assert.Null(await handle.QueryAsync(workflow => workflow.GetPendingApproval()));
        Assert.Equal(scheduledBefore, await CountScheduledActivitiesAsync(handle));

        var followUp = await proxy.RunAsync("Healthy after invalid approval", session);
        Assert.Contains("Echo [2]: Healthy after invalid approval", followUp.Messages[0].Text);

        var eventTypes = new List<EventType>();
        await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
        {
            eventTypes.Add(historyEvent.EventType);
        }
        Assert.DoesNotContain(EventType.WorkflowTaskFailed, eventTypes);
        Assert.DoesNotContain(EventType.WorkflowTaskTimedOut, eventTypes);
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

            // Timeout decisions are retained, so identical retries are idempotent.
            var timeoutRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = decision.RequestId,
                    Approved = false,
                    Reason = decision.Reason,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, timeoutRetry.Status);

            var timeoutConflict = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = decision.RequestId,
                    Approved = true,
                    Reason = decision.Reason,
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, timeoutConflict.Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ResolveApproval_RetryAndConflict_ReturnExpectedStatuses()
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

            var approvalDecision = new DurableApprovalDecision
            {
                RequestId = approvalRequest.RequestId,
                Approved = true,
                Reason = "Looks good!",
            };
            var accepted = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(approvalDecision));
            Assert.Equal(DurableApprovalResolutionStatus.Accepted, accepted.Status);

            var decision = await approvalTask;

            Assert.True(decision.Approved);
            Assert.Equal(approvalRequest.RequestId, decision.RequestId);
            Assert.Equal("Looks good!", decision.Reason);

            // The same decision is retry-safe; a changed decision conflicts.
            var typedRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(approvalDecision));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, typedRetry.Status);

            var changedScope = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = approvalRequest.RequestId,
                    Approved = false,
                    Reason = "Looks good!",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, changedScope.Status);

            // Shared dashboards can retry the same core decision.
            var genericRetry = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = approvalRequest.RequestId,
                    Approved = true,
                    Reason = "Looks good!",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, genericRetry.Status);

            // Resolve a second request through the same shared endpoint.
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
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = genericFirstRequest.RequestId,
                    Approved = true,
                    Reason = "Approved generically.",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, typedAfterGeneric.Status);

            var typedScopeAfterGeneric = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalResolutionResult>(
                wf => wf.ResolveApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = genericFirstRequest.RequestId,
                    Approved = false,
                    Reason = "Approved generically.",
                }));
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, typedScopeAfterGeneric.Status);

            _output.WriteLine("Shared approval retry statuses verified.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<int> CountScheduledActivitiesAsync(WorkflowHandle<AgentWorkflow> handle)
    {
        var count = 0;
        await foreach (var historyEvent in handle.FetchHistoryEventsAsync())
        {
            if (historyEvent.EventType == EventType.ActivityTaskScheduled)
            {
                count++;
            }
        }
        return count;
    }
}
