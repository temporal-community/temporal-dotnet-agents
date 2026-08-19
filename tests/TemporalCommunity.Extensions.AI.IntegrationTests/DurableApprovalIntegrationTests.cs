using Microsoft.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.IntegrationTests.Helpers;
using Xunit;

namespace TemporalCommunity.Extensions.AI.IntegrationTests;

[Collection("AI Integration Tests")]
public class DurableApprovalIntegrationTests
{
    private readonly IntegrationTestFixture _fixture;

    public DurableApprovalIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApprovalFlow_ApproveUnblocksWorkflow()
    {
        var conversationId = $"approval-approve-{Guid.NewGuid():N}";

        // First, start a session so the workflow exists.
        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        // Submit an approval request (simulating what a tool would do inside a workflow).
        var request = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            FunctionName = "delete_records",
            Description = "Delete user data",
        };

        // Request approval in a background task (it blocks until decision).
        var requestTask = RequestApprovalAsync(conversationId, request);

        // Poll deterministically for the pending approval — no fixed-duration sleep.
        // Loop up to 30 × 200ms = 6 s before giving up.
        DurableApprovalRequest? pending = null;
        for (var i = 0; i < 30 && pending is null; i++)
        {
            await Task.Delay(200);
            pending = await _fixture.SessionClient.GetPendingApprovalAsync(conversationId);
        }

        // Guarantee the background approval task is always unblocked on any exit path
        // (assertion failure or success) so the workflow never stays parked on its
        // 7-day WaitConditionAsync and never leaks into the shared fixture environment.
        try
        {
            Assert.NotNull(pending);
            Assert.Equal(request.RequestId, pending!.RequestId);

            // Submit approval decision.
            var decision = new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = true,
                Reason = "Approved by test",
            };

            var resolution = await _fixture.SessionClient.ResolveApprovalAsync(conversationId, decision);
            Assert.Equal(DurableApprovalResolutionStatus.Accepted, resolution.Status);

            // The request task should complete now.
            var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.Approved);
            Assert.Equal(request.RequestId, result.RequestId);

            var retry = await _fixture.SessionClient.ResolveApprovalAsync(conversationId, decision);
            Assert.Equal(DurableApprovalResolutionStatus.AlreadyResolved, retry.Status);

            var conflict = await _fixture.SessionClient.ResolveApprovalAsync(
                conversationId,
                new DurableApprovalDecision
                {
                    RequestId = request.RequestId,
                    Approved = false,
                    Reason = "Conflicting retry",
                });
            Assert.Equal(DurableApprovalResolutionStatus.Conflict, conflict.Status);
        }
        finally
        {
            // If requestTask is still running (assertion failed before resolution),
            // inject a reject decision to unblock the workflow, then drain the task.
            if (!requestTask.IsCompleted)
            {
                try
                {
                        await _fixture.SessionClient.ResolveApprovalAsync(
                        conversationId,
                        new DurableApprovalDecision
                        {
                            RequestId = request.RequestId,
                            Approved = false,
                            Reason = "Test cleanup — forced reject to unblock",
                        });
                }
                catch
                {
                    // Best-effort: ignore errors from a cleanup reject.
                }

                try { await requestTask.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* drain */ }
            }
        }
    }

    [Fact]
    public async Task ApprovalFlow_RejectUnblocksWorkflow()
    {
        var conversationId = $"approval-reject-{Guid.NewGuid():N}";

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        var request = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            FunctionName = "dangerous_operation",
        };

        // Request approval in a background task (it blocks until decision).
        var requestTask = RequestApprovalAsync(conversationId, request);

        // Poll deterministically for the pending approval — no fixed-duration sleep.
        // Without this poll the resolution update below could arrive BEFORE the
        // workflow's WaitConditionAsync is entered, leaving the workflow permanently
        // parked. 30 × 200ms = 6 s deadline.
        DurableApprovalRequest? pending = null;
        for (var i = 0; i < 30 && pending is null; i++)
        {
            await Task.Delay(200);
            pending = await _fixture.SessionClient.GetPendingApprovalAsync(conversationId);
        }

        // Guarantee cleanup on any exit path so the shared fixture stays clean.
        try
        {
            Assert.NotNull(pending);
            Assert.Equal(request.RequestId, pending!.RequestId);

            var decision = new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = false,
                Reason = "Too risky",
            };

            await _fixture.SessionClient.ResolveApprovalAsync(conversationId, decision);

            var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(result.Approved);
            Assert.Equal("Too risky", result.Reason);
        }
        finally
        {
            if (!requestTask.IsCompleted)
            {
                try
                {
                        await _fixture.SessionClient.ResolveApprovalAsync(
                        conversationId,
                        new DurableApprovalDecision
                        {
                            RequestId = request.RequestId,
                            Approved = false,
                            Reason = "Test cleanup — forced reject to unblock",
                        });
                }
                catch
                {
                    // Best-effort: ignore errors from a cleanup reject.
                }

                try { await requestTask.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* drain */ }
            }
        }
    }

    [Fact]
    public async Task GetPendingApproval_ReturnsNullWhenNoPending()
    {
        var conversationId = $"approval-none-{Guid.NewGuid():N}";

        await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        var pending = await _fixture.SessionClient.GetPendingApprovalAsync(conversationId);
        Assert.Null(pending);
    }

    [Fact]
    public async Task ApprovalUpdateWithStart_InitializesBeforeResolverAwait_AndSessionRemainsHealthy()
    {
        var conversationId = $"approval-early-{Guid.NewGuid():N}";
        var workflowId = _fixture.SessionClient.GetWorkflowId(conversationId);
        var input = new DurableChatWorkflowInput
        {
            TimeToLive = TimeSpan.FromMinutes(5),
            ActivityTimeout = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            ApprovalTimeout = TimeSpan.FromMinutes(1),
            // Legacy caller-owned no-tools authority avoids making this test depend on the
            // fixture's worker-owned toolset catalog; initialization ordering is the subject.
            ToolActivityOptions = new Dictionary<string, Temporalio.Workflows.ActivityOptions>(),
        };
        var request = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            Description = "Early update",
        };
        var start = WithStartWorkflowOperation.Create(
            (DurableChatWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, IntegrationTestFixture.TaskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            });

        var first = _fixture.Client.ExecuteUpdateWithStartWorkflowAsync<DurableChatWorkflow, DurableApprovalDecision>(
            workflow => workflow.RequestApprovalAsync(request),
            new WorkflowUpdateWithStartOptions(start));

        var handle = _fixture.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        DurableApprovalRequest? pending = null;
        for (var attempt = 0; attempt < 30 && pending is null; attempt++)
        {
            await Task.Delay(100);
            try
            {
                pending = await handle.QueryAsync(workflow => workflow.GetPendingApproval());
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // The update-with-start RPC is still being admitted.
            }
        }
        var history = pending is null ? await handle.FetchHistoryAsync() : null;
        var workflowTaskFailures = history?.Events
            .Select(item => item.WorkflowTaskFailedEventAttributes?.Failure?.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message));
        Assert.True(
            pending is not null,
            $"Approval did not become pending. Update status: {first.Status}; " +
            $"error: {(first.Exception?.GetBaseException().Message ?? "none")}; " +
            $"workflow task failures: {string.Join(" | ", workflowTaskFailures ?? [])}");
        Assert.Equal(request.RequestId, pending.RequestId);

        var secondRequest = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            Description = "Concurrent request",
        };
        var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(workflow => workflow.RequestApprovalAsync(secondRequest)));
        var conflictFailure = Assert.IsType<ApplicationFailureException>(conflict.InnerException);
        Assert.Equal(DurableApprovalMixin.AlreadyPendingErrorType, conflictFailure.ErrorType);
        Assert.True(conflictFailure.NonRetryable);

        var resolution = await handle.ExecuteUpdateAsync(workflow => workflow.ResolveApprovalAsync(
            new DurableApprovalDecision { RequestId = request.RequestId, Approved = false }));
        Assert.Equal(DurableApprovalResolutionStatus.Accepted, resolution.Status);
        Assert.False((await first).Approved);

        var response = await _fixture.SessionClient.SendAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Still healthy")]);
        Assert.NotEmpty(response.Messages);
    }

    /// <summary>
    /// Sends a RequestApproval update to the workflow. This blocks until a decision is submitted.
    /// </summary>
    private async Task<DurableApprovalDecision> RequestApprovalAsync(
        string conversationId, DurableApprovalRequest request)
    {
        var workflowId = _fixture.SessionClient.GetWorkflowId(conversationId);
        var handle = _fixture.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        return await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableApprovalDecision>(
            wf => wf.RequestApprovalAsync(request));
    }
}
