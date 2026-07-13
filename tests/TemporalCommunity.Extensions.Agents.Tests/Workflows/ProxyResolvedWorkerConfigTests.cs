using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Workflows;

public class ProxyResolvedWorkerConfigTests
{
    [Fact]
    public void Construct_WithRequiredFields_Succeeds()
    {
        var config = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 12,
            ToolActivityOptions = new Dictionary<string, ActivityOptions>(),
        };

        Assert.Equal(12, config.MaxToolCallsPerTurn);
        Assert.Empty(config.ToolActivityOptions);
        Assert.Null(config.DefaultChatClientFactoryKey);
    }

    [Fact]
    public void Construct_WithPlaceholderFields_PreservesValues()
    {
        // The placeholder field is nullable / non-required by design so the record can be
        // constructed from existing resolution paths before that future capability lands.
        var config = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 20,
            ToolActivityOptions = new Dictionary<string, ActivityOptions>(),
            DefaultChatClientFactoryKey = "tenant-aware",
        };

        Assert.Equal("tenant-aware", config.DefaultChatClientFactoryKey);
    }

    [Fact]
    public void RecordEquality_StructuralEquality()
    {
        var dict = new Dictionary<string, ActivityOptions>();
        var a = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 5,
            ToolActivityOptions = dict,
        };
        var b = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 5,
            ToolActivityOptions = dict,
        };

        Assert.Equal(a, b);
    }
}
