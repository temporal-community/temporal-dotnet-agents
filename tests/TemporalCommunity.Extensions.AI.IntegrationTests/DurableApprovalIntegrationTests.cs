using Microsoft.Extensions.AI;
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

            await _fixture.SessionClient.SubmitApprovalAsync(conversationId, decision);

            // The request task should complete now.
            var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.Approved);
            Assert.Equal(request.RequestId, result.RequestId);
        }
        finally
        {
            // If requestTask is still running (assertion failed before SubmitApprovalAsync),
            // inject a reject decision to unblock the workflow, then drain the task.
            if (!requestTask.IsCompleted)
            {
                try
                {
                    await _fixture.SessionClient.SubmitApprovalAsync(
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
        // Without this poll the SubmitApprovalAsync below could arrive BEFORE the
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

            await _fixture.SessionClient.SubmitApprovalAsync(conversationId, decision);

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
                    await _fixture.SessionClient.SubmitApprovalAsync(
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
