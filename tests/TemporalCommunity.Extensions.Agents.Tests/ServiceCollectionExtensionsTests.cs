using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Client;
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

    [Fact]
    public void AddTemporalAgentProxies_RegistersKeyedProxyForName()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());

        services.AddTemporalAgentProxies("agents", "weather");

        using var provider = services.BuildServiceProvider();
        Assert.IsType<TemporalAIAgentProxy>(provider.GetRequiredKeyedService<AIAgent>("weather"));
    }

    [Fact]
    public void AddTemporalAgentProxies_RejectsExistingKeyCaseInsensitively()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<AIAgent>("Weather", (_, _) => throw new NotImplementedException());

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddTemporalAgentProxies("agents", "weather"));

        Assert.Contains("already registered", exception.Message);
        Assert.Contains("weather", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddTemporalAgentProxies_RequiresAtLeastOneName()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddTemporalAgentProxies("agents"));

        Assert.Contains("at least one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
