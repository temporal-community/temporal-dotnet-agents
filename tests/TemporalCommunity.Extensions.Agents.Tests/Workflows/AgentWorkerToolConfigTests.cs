using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class AgentWorkerToolConfigTests
{
    [Fact]
    public void Construct_WithRequiredFields_Succeeds()
    {
        var config = new AgentWorkerToolConfig
        {
            MaxToolCallsPerTurn = 12,
            ToolActivityOptions = new Dictionary<string, ActivityOptions>(),
        };

        Assert.Equal(12, config.MaxToolCallsPerTurn);
        Assert.Empty(config.ToolActivityOptions);
    }

    [Fact]
    public void RecordEquality_StructuralEquality()
    {
        var dict = new Dictionary<string, ActivityOptions>();
        var a = new AgentWorkerToolConfig
        {
            MaxToolCallsPerTurn = 5,
            ToolActivityOptions = dict,
        };
        var b = new AgentWorkerToolConfig
        {
            MaxToolCallsPerTurn = 5,
            ToolActivityOptions = dict,
        };

        Assert.Equal(a, b);
    }
}
