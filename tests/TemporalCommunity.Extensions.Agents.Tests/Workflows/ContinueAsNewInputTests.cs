using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class ContinueAsNewInputTests
{
    [Fact]
    public void CreateContinueAsNewInput_PreservesRenewedAgentSessionIdleDeadline()
    {
        var originalDeadline = DateTimeOffset.UnixEpoch.AddMinutes(2);
        var renewedDeadline = originalDeadline.AddMinutes(10);
        var original = new AgentWorkflowInput
        {
            AgentName = "support",
            TaskQueue = "agents",
            SessionIdleDeadline = originalDeadline,
        };

        var cloned = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            original, [], [], DateTimeOffset.UnixEpoch, [], completedTurnCount: 1,
            sessionIdleDeadline: renewedDeadline);

        var actual = Assert.IsType<AgentWorkflowInput>(cloned);
        Assert.Equal(renewedDeadline, actual.SessionIdleDeadline);
        Assert.Equal(originalDeadline, original.SessionIdleDeadline);
    }

    [Fact]
    public void CreateContinueAsNewInput_PreservesDerivedAgentInputRuntimeType()
    {
        var original = new AgentWorkflowInput
        {
            AgentName = "support",
            TaskQueue = "agents",
            HistoryReducerKey = "agent-reducer",
            SessionEpoch = "agent-epoch",
            MaxTerminalOutcomeCount = 32,
            MaxTerminalOutcomeBytes = 4096,
        };
        var history = new List<DurableSessionEntry>();
        IReadOnlyList<DurableApprovalDecision> approvals = [];

        var cloned = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            original, history, approvals, DateTimeOffset.UnixEpoch, [], completedTurnCount: 5);

        var actual = Assert.IsType<AgentWorkflowInput>(cloned);
        Assert.Equal("support", actual.AgentName);
        Assert.Equal("agents", actual.TaskQueue);
        Assert.Equal("agent-reducer", actual.HistoryReducerKey);
        Assert.Equal("agent-epoch", actual.SessionEpoch);
        Assert.Equal(5, actual.CompletedTurnCount);
        Assert.Equal(32, actual.MaxTerminalOutcomeCount);
        Assert.Equal(4096, actual.MaxTerminalOutcomeBytes);
        Assert.Same(history, actual.CarriedHistory);
    }
}
