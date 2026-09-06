using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowAgents"/>.
/// The methods on this type require a Temporal workflow context — we can verify the supporting
/// types compile and the static helpers exist with the expected signatures, but we cannot
/// invoke them here. <see cref="WorkflowAgentsGuardTests"/> pins the
/// outside-workflow guard behavior.
/// </summary>
public class WorkflowAgentsTests
{
    [Fact]
    public void ExecuteAgentsInParallelAsync_IsPublicStaticMethod()
    {
        // Verify the method exists with the expected signature so callers can discover it.
        var method = typeof(WorkflowAgents).GetMethod(
            nameof(WorkflowAgents.ExecuteAgentsInParallelAsync));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void GetTemporalAgent_IsPublicStaticMethod()
    {
        // GetTemporalAgent is now guarded with a runtime workflow-context check, so we can't invoke
        // it from a unit test. Verify the surface remains discoverable via reflection.
        var method = typeof(WorkflowAgents).GetMethod(
            nameof(WorkflowAgents.GetTemporalAgent));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void NewAgentSessionId_IsPublicStaticMethod()
    {
        var method = typeof(WorkflowAgents).GetMethod(
            nameof(WorkflowAgents.NewAgentSessionId));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }
}
