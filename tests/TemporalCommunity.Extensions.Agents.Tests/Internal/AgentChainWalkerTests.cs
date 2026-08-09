using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Internal;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

public class AgentChainWalkerTests
{
    [Fact]
    public void WalkAIAgent_NullRoot_ReturnsEmpty()
    {
        Assert.Empty(AgentChainWalker.WalkAIAgent(null));
    }

    [Fact]
    public void WalkAIAgent_ThreeDeepChain_YieldsAllLinksInOrder()
    {
        var leaf = new MarkerAIAgent();
        var middle = new PassThroughDelegatingAIAgent(leaf);
        var outer = new PassThroughDelegatingAIAgent(middle);

        var result = AgentChainWalker.WalkAIAgent(outer).ToList();

        Assert.Equal([outer, middle, leaf], result);
    }

    [Fact]
    public void Contains_AIAgent_ReturnsTrueWhenTypePresent()
    {
        var leaf = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);

        Assert.True(AgentChainWalker.Contains<MarkerAIAgent>(outer));
        Assert.False(AgentChainWalker.Contains<UnrelatedAIAgent>(outer));
    }

    [Fact]
    public void FindFirst_AIAgent_ReturnsInnermostMatch()
    {
        var leaf = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);

        Assert.Same(leaf, AgentChainWalker.FindFirst<MarkerAIAgent>(outer));
    }

    [Fact]
    public void ContainsReference_AIAgent_UsesExactIdentity()
    {
        var leaf = new MarkerAIAgent();
        var sameTypeButDifferentInstance = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);

        Assert.True(AgentChainWalker.ContainsReference(outer, leaf));
        Assert.False(AgentChainWalker.ContainsReference(outer, sameTypeButDifferentInstance));
    }

    [Fact]
    public void Contains_OpenTelemetryAgent_DetectsWhenPresent()
    {
        var wrapped = new AIAgentBuilder(new MarkerAIAgent()).UseOpenTelemetry().Build();

        Assert.True(AgentChainWalker.Contains<OpenTelemetryAgent>(wrapped));
    }

    private class MarkerAIAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentSession>(new NotImplementedException());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<JsonElement>(new NotImplementedException());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentSession>(new NotImplementedException());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse());

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate();
            await Task.CompletedTask;
        }
    }

    private sealed class UnrelatedAIAgent : MarkerAIAgent;

    private sealed class PassThroughDelegatingAIAgent(AIAgent inner) : DelegatingAIAgent(inner)
    {
    }
}
