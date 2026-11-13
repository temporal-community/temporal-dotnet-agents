// HARNESS for docs/how-to/MAF/testing-agents.md § "Testing TemporalAgentsOptions Configuration".
//
// The registration tests are compiled, not discovered or executed.
namespace DocSnippets.TestingAgents;

// BEGIN SNIPPET docs/how-to/MAF/testing-agents.md#testing-temporalagentsoptions-configuration (lines 207-255)
using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.Agents;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Xunit;

public sealed class TemporalAgentsOptionsTests
{
    [Fact]
    public void AddTemporalAgents_RegistersKeyedAIAgentProxies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        services.AddChatClient(A.Fake<IChatClient>());

        services
            .AddHostedTemporalWorker("test-queue")
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("AgentA", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>());
                opts.AddDurableAgent("AgentB", agent =>
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>());
            });

        using var sp = services.BuildServiceProvider();

        var proxyA = sp.GetTemporalAgentProxy("AgentA");
        var proxyB = sp.GetTemporalAgentProxy("AgentB");

        Assert.NotNull(proxyA);
        Assert.NotNull(proxyB);
        Assert.Same(proxyA, sp.GetTemporalAgentProxy("AgentA"));
    }

    [Fact]
    public void AddTemporalAgents_ThrowsOnNullConfigure()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());

        var builder = services.AddHostedTemporalWorker("q");

        Assert.Throws<ArgumentNullException>(
            () => builder.AddTemporalAgents(null!));
    }
}
// END SNIPPET docs/how-to/MAF/testing-agents.md#testing-temporalagentsoptions-configuration
