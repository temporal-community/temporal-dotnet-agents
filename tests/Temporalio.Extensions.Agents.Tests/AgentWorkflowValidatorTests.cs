using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class AgentWorkflowValidatorTests
{
    [Fact]
    public void ValidateRunAgent_NullMessages_ThrowsArgumentException()
    {
        var workflow = new AgentWorkflow();
        var request = new RunRequest(messages: null!, responseFormat: null);

        Assert.Throws<ArgumentException>(() => workflow.ValidateRunAgent(request));
    }

    [Fact]
    public void ValidateRunAgent_EmptyMessages_ThrowsArgumentException()
    {
        var workflow = new AgentWorkflow();
        var request = new RunRequest(messages: [], responseFormat: null);

        Assert.Throws<ArgumentException>(() => workflow.ValidateRunAgent(request));
    }

    [Fact]
    public void ValidateRunAgent_NullRequest_ThrowsArgumentException()
    {
        var workflow = new AgentWorkflow();
        Assert.Throws<ArgumentException>(() => workflow.ValidateRunAgent(null!));
    }

    [Fact]
    public void ValidateRunAgent_ValidRequest_DoesNotThrow()
    {
        var workflow = new AgentWorkflow();
        var request = new RunRequest("Hello");

        // Should not throw.
        workflow.ValidateRunAgent(request);
    }

    [Fact]
    public void ValidateRequestApproval_NullRequest_ThrowsArgumentNullException()
    {
        var workflow = new AgentWorkflow();
        Assert.Throws<ArgumentNullException>(() => workflow.ValidateRequestApproval(null!));
    }

    [Fact]
    public void ValidateRequestApproval_EmptyRequestId_ThrowsArgumentException()
    {
        var workflow = new AgentWorkflow();
        var request = new ApprovalRequest { RequestId = string.Empty, Action = "Delete" };
        Assert.Throws<ArgumentException>(() => workflow.ValidateRequestApproval(request));
    }

    [Fact]
    public void ValidateRequestApproval_ValidRequest_DoesNotThrow()
    {
        var workflow = new AgentWorkflow();
        var request = new ApprovalRequest { Action = "Delete records" };

        // Should not throw — RequestId has a default auto-generated value.
        workflow.ValidateRequestApproval(request);
    }

    [Fact]
    public void ValidateSubmitApproval_NullDecision_ThrowsArgumentNullException()
    {
        var workflow = new AgentWorkflow();
        Assert.Throws<ArgumentNullException>(() => workflow.ValidateSubmitApproval(null!));
    }

    [Fact]
    public void ValidateSubmitApproval_NoPendingApproval_ThrowsInvalidOperationException()
    {
        var workflow = new AgentWorkflow();
        var decision = new ApprovalDecision { RequestId = "abc123", Approved = true };
        Assert.Throws<InvalidOperationException>(() => workflow.ValidateSubmitApproval(decision));
    }
}
