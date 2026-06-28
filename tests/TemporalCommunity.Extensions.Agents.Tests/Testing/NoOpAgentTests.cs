using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Testing;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Testing;

public class NoOpAgentTests
{
    [Fact]
    public void Instance_ReturnsSameSingletonAcrossCalls()
    {
        var a = NoOpAgent.Instance;
        var b = NoOpAgent.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void Instance_HasNameAndDescription()
    {
        var agent = NoOpAgent.Instance;
        Assert.NotNull(agent.Name);
        Assert.NotNull(agent.Description);
    }

    [Fact]
    public void Constructor_AllowsAlternateInstances()
    {
        // Public ctor exists for scenarios that need reference identity.
        var a = new NoOpAgent();
        var b = new NoOpAgent();
        Assert.NotSame(a, b);
        Assert.NotSame(a, NoOpAgent.Instance);
    }

    [Fact]
    public async Task RunAsync_ReturnsEmptyResponse_DoesNotThrow()
    {
        var response = await NoOpAgent.Instance.RunAsync(
            new[] { new ChatMessage(ChatRole.User, "hello") });
        Assert.NotNull(response);
    }

    [Fact]
    public async Task RunStreamingAsync_YieldsNothing_DoesNotThrow()
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in NoOpAgent.Instance.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "hello") }))
        {
            updates.Add(update);
        }
        Assert.Empty(updates);
    }

    [Fact]
    public async Task CreateSessionAsync_ProducesUsableSession()
    {
        var session = await NoOpAgent.Instance.CreateSessionAsync();
        Assert.NotNull(session);
    }

    [Fact]
    public void CanBeWrappedByAIAgentBuilder()
    {
        // Validates the primary use case: NoOpAgent can serve as the inner agent
        // for an AIAgentBuilder pipeline.
        var builder = new AIAgentBuilder(NoOpAgent.Instance);
        var built = builder.Build();
        Assert.NotNull(built);
    }
}
