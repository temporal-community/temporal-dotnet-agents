#pragma warning disable TA002 // compaction surface is experimental

using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.Compaction;

/// <summary>
/// Step 6b tests: pin the activity → workflow signaling shape for compaction triggers.
/// </summary>
public class AgentStepResultCompactionFlagsTests
{
    [Fact]
    public void AgentStepResult_CompactionNeeded_DefaultsToFalse()
    {
        // Compaction is opt-in via UseCompaction; the default agent step result must NOT
        // signal a trigger.
        var result = new AgentStepResult
        {
            IsFinal = true,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, "ok"),
        };

        Assert.False(result.CompactionNeeded);
        Assert.Null(result.CompactionTargetMessageIds);
    }

    [Fact]
    public void AgentStepResult_CanCarryCompactionTrigger()
    {
        var targets = new[] { "msg-1", "msg-2", "msg-3" };
        var result = new AgentStepResult
        {
            IsFinal = false,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, "calling tool"),
            CompactionNeeded = true,
            CompactionTargetMessageIds = targets,
        };

        Assert.True(result.CompactionNeeded);
        Assert.Same(targets, result.CompactionTargetMessageIds);
    }
}
