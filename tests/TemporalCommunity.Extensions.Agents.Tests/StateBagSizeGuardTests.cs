using TemporalCommunity.Extensions.Agents.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Tests that the <c>StateBagSizeWarnThresholdBytes</c> constant shipped in
/// <see cref="AgentWorkflow"/> is correctly defined and matches the specified default.
/// </summary>
public class StateBagSizeGuardTests
{
    [Fact]
    public void StateBagSizeWarnThreshold_Is64KB()
    {
        Assert.Equal(64 * 1024, AgentWorkflow.StateBagSizeWarnThresholdBytes);
    }
}
