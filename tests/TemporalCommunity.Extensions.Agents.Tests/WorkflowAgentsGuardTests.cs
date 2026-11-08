using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Pins that deterministic workflow-only ID generation fails fast outside workflow context.
/// </summary>
public class WorkflowAgentsGuardTests
{
    [Fact]
    public void NewAgentSessionId_OutsideWorkflow_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkflowAgents.NewAgentSessionId("WeatherAgent"));
        Assert.Contains("NewAgentSessionId", ex.Message, StringComparison.Ordinal);
        // Surface the recommended alternative (TemporalAgentSessionId.WithRandomKey).
        Assert.Contains("WithRandomKey", ex.Message, StringComparison.Ordinal);
    }

}
