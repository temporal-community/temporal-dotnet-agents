using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI.Exceptions;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests.Internal;

public class TemporalSessionBoundaryAgentTests
{
    [Fact]
    public async Task RunAsync_RequiresExactDurableSessionAndRestoresItsRunContext()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var leaf = new RecordingLeafAgent();
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);

        await boundary.RunAsync(Messages, durableSession);

        Assert.Null(leaf.NonStreamingSession);
        Assert.Null(leaf.NonStreamingRunContextSession);
        Assert.Same(durableSession, AIAgent.CurrentRunContext?.Session);
    }

    [Fact]
    public async Task RunStreamingAsync_RestoresDurableRunContextBeforeAndAfterEveryYield()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var leaf = new RecordingLeafAgent();
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);
        var observedByOuterMiddleware = new List<AgentSession?>();
        var outer = new ContextObservingAgent(boundary, observedByOuterMiddleware);

        await foreach (var _ in outer.RunStreamingAsync(Messages, durableSession))
        {
        }

        Assert.Null(leaf.StreamingSession);
        Assert.Null(leaf.StreamingRunContextSession);
        Assert.NotEmpty(observedByOuterMiddleware);
        Assert.All(observedByOuterMiddleware, observed => Assert.Same(durableSession, observed));
    }

    [Fact]
    public async Task RunAsync_RejectsNullOrReplacementSessionBeforeLeafInvocation()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var replacement = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var leaf = new RecordingLeafAgent();
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);

        await Assert.ThrowsAsync<DurableConfigurationException>(
            () => boundary.RunAsync(Messages, session: null));
        await Assert.ThrowsAsync<DurableConfigurationException>(
            () => boundary.RunAsync(Messages, replacement));

        Assert.Equal(0, leaf.NonStreamingCalls);
    }

    [Fact]
    public async Task SessionLifecycleApis_AreExplicitlyUnsupported()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var boundary = new TemporalSessionBoundaryAgent(new RecordingLeafAgent(), durableSession);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await boundary.CreateSessionAsync());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await boundary.SerializeSessionAsync(durableSession));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await boundary.DeserializeSessionAsync(JsonDocument.Parse("{}").RootElement));
    }

    private static readonly IReadOnlyList<ChatMessage> Messages =
        [new ChatMessage(ChatRole.User, "hello")];

    private sealed class RecordingLeafAgent : AIAgent
    {
        internal int NonStreamingCalls { get; private set; }
        internal AgentSession? NonStreamingSession { get; private set; }
        internal AgentSession? NonStreamingRunContextSession { get; private set; }
        internal AgentSession? StreamingSession { get; private set; }
        internal AgentSession? StreamingRunContextSession { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            new(new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("leaf")));

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingCalls++;
            NonStreamingSession = session;
            NonStreamingRunContextSession = CurrentRunContext?.Session;
            return Task.FromResult(new AgentResponse());
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingSession = session;
            StreamingRunContextSession = CurrentRunContext?.Session;
            await Task.CompletedTask;
            yield return new AgentResponseUpdate();
        }
    }

    private sealed class ContextObservingAgent(
        AIAgent inner,
        List<AgentSession?> observations) : DelegatingAIAgent(inner)
    {
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            observations.Add(CurrentRunContext?.Session);
            await foreach (var update in base.RunCoreStreamingAsync(
                messages,
                session,
                options,
                cancellationToken))
            {
                observations.Add(CurrentRunContext?.Session);
                yield return update;
                observations.Add(CurrentRunContext?.Session);
            }

            observations.Add(CurrentRunContext?.Session);
        }
    }
}
