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
    public void CreateContinueAsNewInput_PreservesDerivedAgentInputRuntimeType()
    {
        var original = new AgentWorkflowInput
        {
            AgentName = "support",
            TaskQueue = "agents",
            HistoryReducerKey = "agent-reducer",
        };
        var history = new List<DurableSessionEntry>();
        IReadOnlyList<DurableApprovalDecision> approvals = [];

        var cloned = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            original, history, approvals, DateTimeOffset.UnixEpoch);

        var actual = Assert.IsType<AgentWorkflowInput>(cloned);
        Assert.Equal("support", actual.AgentName);
        Assert.Equal("agents", actual.TaskQueue);
        Assert.Equal("agent-reducer", actual.HistoryReducerKey);
        Assert.Same(history, actual.CarriedHistory);
    }
}
