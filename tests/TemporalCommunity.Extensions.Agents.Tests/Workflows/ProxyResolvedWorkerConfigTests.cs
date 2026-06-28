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
            UseExternalStoreMode = true,
            ToolActivityOptions = new Dictionary<string, ActivityOptions>(),
        };

        Assert.Equal(12, config.MaxToolCallsPerTurn);
        Assert.True(config.UseExternalStoreMode);
        Assert.Empty(config.ToolActivityOptions);
        Assert.Null(config.DefaultChatClientFactoryKey);
        Assert.Null(config.CompactionStrategyKey);
    }

    [Fact]
    public void Construct_WithPlaceholderFields_PreservesValues()
    {
        // The placeholder fields are reserved for Steps 4 and 6 of the maf-gap plan.
        // They are nullable / non-required by design so the record can be constructed
        // from existing Fix-4 resolution paths before those steps land. This test pins
        // the contract so the placeholders don't accidentally become required.
        var config = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 20,
            UseExternalStoreMode = false,
            ToolActivityOptions = new Dictionary<string, ActivityOptions>(),
            DefaultChatClientFactoryKey = "tenant-aware",
            CompactionStrategyKey = "summarization",
        };

        Assert.Equal("tenant-aware", config.DefaultChatClientFactoryKey);
        Assert.Equal("summarization", config.CompactionStrategyKey);
    }

    [Fact]
    public void RecordEquality_StructuralEquality()
    {
        var dict = new Dictionary<string, ActivityOptions>();
        var a = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 5,
            UseExternalStoreMode = true,
            ToolActivityOptions = dict,
        };
        var b = new ProxyResolvedWorkerConfig
        {
            MaxToolCallsPerTurn = 5,
            UseExternalStoreMode = true,
            ToolActivityOptions = dict,
        };

        Assert.Equal(a, b);
    }
}
