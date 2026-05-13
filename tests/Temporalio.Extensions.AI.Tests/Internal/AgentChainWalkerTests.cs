using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI.Internal;
using Xunit;

namespace Temporalio.Extensions.AI.Tests.Internal;

public class AgentChainWalkerTests
{
    // ====================================================================
    // WalkChatClient
    // ====================================================================

    [Fact]
    public void WalkChatClient_NullRoot_ReturnsEmpty()
    {
        var result = AgentChainWalker.WalkChatClient(null).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void WalkChatClient_NonDelegatingRoot_YieldsOnlyRoot()
    {
        var leaf = new MarkerChatClient();
        var result = AgentChainWalker.WalkChatClient(leaf).ToList();
        Assert.Single(result);
        Assert.Same(leaf, result[0]);
    }

    [Fact]
    public void WalkChatClient_ThreeDeepChain_YieldsAllLinksInOrder()
    {
        var leaf = new MarkerChatClient();
        var mid = new PassThroughDelegatingChatClient(leaf);
        var outer = new PassThroughDelegatingChatClient(mid);

        var result = AgentChainWalker.WalkChatClient(outer).ToList();

        Assert.Equal(3, result.Count);
        Assert.Same(outer, result[0]);
        Assert.Same(mid, result[1]);
        Assert.Same(leaf, result[2]);
    }

    // Note: cycle-protection is verified structurally — the walker uses a
    // HashSet<object>(ReferenceEqualityComparer.Instance) and `visited.Add()` short-circuits
    // a re-encounter. External assertion would require mutating the protected InnerClient
    // setter, which MAF doesn't expose; the walker's source code is the contract.

    // ====================================================================
    // WalkAIAgent
    // ====================================================================

    [Fact]
    public void WalkAIAgent_NullRoot_ReturnsEmpty()
    {
        var result = AgentChainWalker.WalkAIAgent(null).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void WalkAIAgent_NonDelegatingRoot_YieldsOnlyRoot()
    {
        var leaf = new MarkerAIAgent();
        var result = AgentChainWalker.WalkAIAgent(leaf).ToList();
        Assert.Single(result);
        Assert.Same(leaf, result[0]);
    }

    [Fact]
    public void WalkAIAgent_ThreeDeepChain_YieldsAllLinksInOrder()
    {
        var leaf = new MarkerAIAgent();
        var mid = new PassThroughDelegatingAIAgent(leaf);
        var outer = new PassThroughDelegatingAIAgent(mid);

        var result = AgentChainWalker.WalkAIAgent(outer).ToList();

        Assert.Equal(3, result.Count);
        Assert.Same(outer, result[0]);
        Assert.Same(mid, result[1]);
        Assert.Same(leaf, result[2]);
    }

    // ====================================================================
    // Contains<T> (chat client)
    // ====================================================================

    [Fact]
    public void Contains_ChatClient_ReturnsTrueWhenTypePresent()
    {
        var leaf = new MarkerChatClient();
        var outer = new PassThroughDelegatingChatClient(leaf);
        Assert.True(AgentChainWalker.Contains<MarkerChatClient>(outer));
    }

    [Fact]
    public void Contains_ChatClient_ReturnsFalseWhenTypeAbsent()
    {
        var leaf = new MarkerChatClient();
        var outer = new PassThroughDelegatingChatClient(leaf);
        Assert.False(AgentChainWalker.Contains<UnrelatedChatClient>(outer));
    }

    [Fact]
    public void Contains_ChatClient_NullRoot_ReturnsFalse()
    {
        Assert.False(AgentChainWalker.Contains<MarkerChatClient>((IChatClient?)null));
    }

    // ====================================================================
    // Contains<T> (agent)
    // ====================================================================

    [Fact]
    public void Contains_AIAgent_ReturnsTrueWhenTypePresent()
    {
        var leaf = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);
        Assert.True(AgentChainWalker.Contains<MarkerAIAgent>(outer));
    }

    [Fact]
    public void Contains_AIAgent_ReturnsFalseWhenTypeAbsent()
    {
        var leaf = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);
        Assert.False(AgentChainWalker.Contains<UnrelatedAIAgent>(outer));
    }

    // ====================================================================
    // FindFirst<T>
    // ====================================================================

    [Fact]
    public void FindFirst_ChatClient_ReturnsInstanceWhenPresent()
    {
        var leaf = new MarkerChatClient();
        var outer = new PassThroughDelegatingChatClient(leaf);

        var match = AgentChainWalker.FindFirst<MarkerChatClient>(outer);

        Assert.NotNull(match);
        Assert.Same(leaf, match);
    }

    [Fact]
    public void FindFirst_ChatClient_ReturnsNullWhenAbsent()
    {
        var leaf = new MarkerChatClient();
        var outer = new PassThroughDelegatingChatClient(leaf);

        var match = AgentChainWalker.FindFirst<UnrelatedChatClient>(outer);

        Assert.Null(match);
    }

    [Fact]
    public void FindFirst_AIAgent_ReturnsInstanceWhenPresent()
    {
        var leaf = new MarkerAIAgent();
        var outer = new PassThroughDelegatingAIAgent(leaf);

        var match = AgentChainWalker.FindFirst<MarkerAIAgent>(outer);

        Assert.NotNull(match);
        Assert.Same(leaf, match);
    }

    // ====================================================================
    // Test fixtures
    // ====================================================================

    private sealed class MarkerChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(MarkerChatClient) ? this : null;
    }

    private sealed class UnrelatedChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class PassThroughDelegatingChatClient : DelegatingChatClient
    {
        public PassThroughDelegatingChatClient(IChatClient inner) : base(inner) { }
    }

    private sealed class MarkerAIAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
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

    private sealed class UnrelatedAIAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
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

    private sealed class PassThroughDelegatingAIAgent : DelegatingAIAgent
    {
        public PassThroughDelegatingAIAgent(AIAgent inner) : base(inner) { }
    }
}
