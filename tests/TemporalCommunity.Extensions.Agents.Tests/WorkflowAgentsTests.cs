using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for deterministic workflow session-ID generation.
/// </summary>
public class WorkflowAgentsTests
{
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
