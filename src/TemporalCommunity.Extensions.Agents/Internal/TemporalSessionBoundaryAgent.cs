using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>
/// Keeps the restored durable session visible to outer MAF middleware while translating to the
/// transient session contract required by the library-created <see cref="ChatClientAgent"/> leaf.
/// </summary>
internal sealed class TemporalSessionBoundaryAgent(
    AIAgent innerAgent,
    TemporalAgentSession durableSession) : DelegatingAIAgent(innerAgent)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedSession(session);
        var durableRunContext = CurrentRunContext;
        try
        {
            return await InnerAgent.RunAsync(
                messages,
                session: null,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CurrentRunContext = durableRunContext;
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureExpectedSession(session);
        var durableRunContext = CurrentRunContext;
        try
        {
            await foreach (var update in InnerAgent.RunStreamingAsync(
                messages,
                session: null,
                options,
                cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                CurrentRunContext = durableRunContext;
                yield return update;
                CurrentRunContext = durableRunContext;
            }
        }
        finally
        {
            CurrentRunContext = durableRunContext;
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default) =>
        throw SessionLifecycleNotSupported();

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw SessionLifecycleNotSupported();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw SessionLifecycleNotSupported();

    private void EnsureExpectedSession(AgentSession? session)
    {
        if (ReferenceEquals(session, durableSession))
        {
            return;
        }

        throw new DurableConfigurationException(
            "Agent middleware replaced or removed the TemporalAgentSession supplied for this " +
            "activity attempt. Middleware must forward the exact session instance so StateBag " +
            "changes can be persisted durably.");
    }

    private static NotSupportedException SessionLifecycleNotSupported() =>
        new(
            "Session lifecycle APIs are not supported on the internal activity pipeline. " +
            "Temporal owns durable session creation and serialization.");
}
