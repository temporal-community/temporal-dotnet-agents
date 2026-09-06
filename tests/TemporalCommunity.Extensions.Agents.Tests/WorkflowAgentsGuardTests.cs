using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Pins the contract that every public method on <see cref="WorkflowAgents"/>
/// fails fast with a clear <see cref="InvalidOperationException"/> when called outside a
/// Temporal workflow. The guards replace the previous <c>[EditorBrowsable(Never)]</c>
/// IntelliSense-hide trick with an actual runtime defense.
/// </summary>
public class WorkflowAgentsGuardTests
{
    [Fact]
    public void GetTemporalAgent_OutsideWorkflow_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkflowAgents.GetTemporalAgent("WeatherAgent"));
        Assert.Contains("GetTemporalAgent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("workflow", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Surface the recommended alternative for external code so users know what to do.
        Assert.Contains("GetTemporalAgentProxy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewAgentSessionId_OutsideWorkflow_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkflowAgents.NewAgentSessionId("WeatherAgent"));
        Assert.Contains("NewAgentSessionId", ex.Message, StringComparison.Ordinal);
        // Surface the recommended alternative (TemporalAgentSessionId.WithRandomKey).
        Assert.Contains("WithRandomKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAgentsInParallelAsync_OutsideWorkflow_Throws()
    {
        var stubAgent = new StubAIAgent("Stub");
        var temporalAgent = TryGetAgentInsideGuard();
        // We can't construct TemporalAIAgent outside a workflow either (it has its own
        // workflow guard now via GetTemporalAgent), so verify ExecuteAgentsInParallelAsync's guard
        // by passing an empty sequence — the guard fires before any iteration.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await WorkflowAgents.ExecuteAgentsInParallelAsync(
                Array.Empty<(TemporalAIAgent, IList<ChatMessage>, AgentSession)>()));
        Assert.Contains("ExecuteAgentsInParallelAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("workflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Helper that simply documents the fact that <see cref="WorkflowAgents.GetTemporalAgent"/>
    /// itself throws outside workflow context — used to express intent in the test above.
    /// </summary>
    private static TemporalAIAgent? TryGetAgentInsideGuard() => null;
}
