using System.Text.Json.Serialization.Metadata;
using TemporalCommunity.Extensions.Agents.State;
using TemporalCommunity.Extensions.Agents.Workflows;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

/// <summary>
/// Verifies that <see cref="AgentSessionJsonContext"/> includes source-gen metadata for the
/// activity I/O types added in v0.3 per-tool-activities redesign. Each test calls
/// <see cref="System.Text.Json.JsonSerializerOptions.GetTypeInfo"/> on the context's options
/// and asserts that the returned <see cref="JsonTypeInfo"/> originated from the generated
/// context. A non-None kind alone is insufficient because a reflection resolver also produces
/// metadata with a non-None kind.
/// </summary>
public class AgentSessionJsonContextTests
{
    [Fact]
    public void AgentStepInput_UsesGeneratedResolver_InDurableOptions()
    {
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentStepInput));
        Assert.NotNull(typeInfo);
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
    }

    [Fact]
    public void AgentStepResult_UsesGeneratedResolver_InDurableOptions()
    {
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentStepResult));
        Assert.NotNull(typeInfo);
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
    }

    [Fact]
    public void InvokeAgentToolInput_UsesGeneratedResolver_InDurableOptions()
    {
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions.GetTypeInfo(typeof(InvokeAgentToolInput));
        Assert.NotNull(typeInfo);
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
    }

    [Theory]
    [InlineData(typeof(AgentSessionRequest))]
    [InlineData(typeof(DurableSessionEntry))]
    public void CompatibilityProtectedTypes_UseGeneratedResolver_InDurableOptions(Type type)
    {
        var typeInfo = TemporalAgentJsonUtilities.DefaultOptions.GetTypeInfo(type);

        Assert.NotNull(typeInfo);
        Assert.Same(AgentSessionJsonContext.Default, typeInfo.OriginatingResolver);
    }
}
