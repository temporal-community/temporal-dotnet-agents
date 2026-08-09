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
    public async Task RunAsync_LeafException_RestoresDurableRunContext()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var expected = new InvalidOperationException("sentinel failure");
        var leaf = new ExitPathLeafAgent(ExitPath.Throw, expected);
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);
        var observations = new List<AgentSession?>();
        var outer = new ExitContextObservingAgent(boundary, observations);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outer.RunAsync(Messages, durableSession));

        Assert.Same(expected, actual);
        Assert.Null(leaf.ObservedSession);
        Assert.Null(leaf.ObservedRunContextSession);
        Assert.NotEmpty(observations);
        Assert.All(observations, observed => Assert.Same(durableSession, observed));
    }

    [Fact]
    public async Task RunStreamingAsync_LeafException_RestoresDurableRunContext()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var expected = new InvalidOperationException("streaming sentinel failure");
        var leaf = new ExitPathLeafAgent(ExitPath.YieldThenThrow, expected);
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);
        var observations = new List<AgentSession?>();
        var outer = new ExitContextObservingAgent(boundary, observations);
        var observedUpdates = 0;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in outer.RunStreamingAsync(Messages, durableSession))
            {
                observedUpdates++;
            }
        });

        Assert.Same(expected, actual);
        Assert.Equal(1, observedUpdates);
        Assert.Null(leaf.ObservedSession);
        Assert.Null(leaf.ObservedRunContextSession);
        Assert.NotEmpty(observations);
        Assert.All(observations, observed => Assert.Same(durableSession, observed));
    }

    [Fact]
    public async Task RunStreamingAsync_Cancellation_RestoresDurableRunContext()
    {
        var durableSession = new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey("boundary"));
        var leaf = new ExitPathLeafAgent(ExitPath.WaitForCancellation);
        var boundary = new TemporalSessionBoundaryAgent(leaf, durableSession);
        var observations = new List<AgentSession?>();
        var outer = new ExitContextObservingAgent(boundary, observations);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in outer.RunStreamingAsync(
                Messages,
                durableSession,
                cancellationToken: cancellation.Token))
            {
            }
        });

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(leaf.ObservedSession);
        Assert.Null(leaf.ObservedRunContextSession);
        Assert.NotEmpty(observations);
        Assert.All(observations, observed => Assert.Same(durableSession, observed));
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

    private enum ExitPath
    {
        Throw,
        YieldThenThrow,
        WaitForCancellation,
    }

    private sealed class ExitPathLeafAgent(
        ExitPath exitPath,
        Exception? exception = null) : AIAgent
    {
        internal AgentSession? ObservedSession { get; private set; }
        internal AgentSession? ObservedRunContextSession { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            RecordContext(session);
            return exitPath == ExitPath.Throw
                ? Task.FromException<AgentResponse>(exception!)
                : Task.FromResult(new AgentResponse());
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RecordContext(session);
            if (exitPath == ExitPath.WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            yield return new AgentResponseUpdate();
            if (exitPath == ExitPath.YieldThenThrow)
            {
                throw exception!;
            }
        }

        private void RecordContext(AgentSession? session)
        {
            ObservedSession = session;
            ObservedRunContextSession = CurrentRunContext?.Session;
        }
    }

    private sealed class ExitContextObservingAgent(
        AIAgent inner,
        List<AgentSession?> observations) : DelegatingAIAgent(inner)
    {
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            observations.Add(CurrentRunContext?.Session);
            try
            {
                return await base.RunCoreAsync(messages, session, options, cancellationToken);
            }
            finally
            {
                observations.Add(CurrentRunContext?.Session);
            }
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            observations.Add(CurrentRunContext?.Session);
            try
            {
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
            }
            finally
            {
                observations.Add(CurrentRunContext?.Session);
            }
        }
    }
}
