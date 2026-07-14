using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

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
        var request = new DurableApprovalRequest { RequestId = string.Empty };
        Assert.Throws<ArgumentException>(() => workflow.ValidateRequestApproval(request));
    }

    [Fact]
    public void ValidateRequestApproval_ValidRequest_DoesNotThrow()
    {
        var workflow = new AgentWorkflow();
        var request = new DurableApprovalRequest { RequestId = "test-id", Description = "Delete records" };

        // Should not throw.
        workflow.ValidateRequestApproval(request);
    }

}
