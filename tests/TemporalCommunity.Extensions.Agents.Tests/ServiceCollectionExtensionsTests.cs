using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void GetTemporalAgentProxy_WhenNameIsNotRegistered_ThrowsAgentNotRegisteredException()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<AgentNotRegisteredException>(
            () => services.GetTemporalAgentProxy("missing-agent"));

        Assert.Contains("missing-agent", exception.Message);
    }
}
