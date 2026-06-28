using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.Agents.Session;

namespace TemporalCommunity.Extensions.Agents.Testing;

/// <summary>
/// A minimal, lightweight <see cref="AIAgent"/> implementation that satisfies the abstract surface
/// without performing any real LLM work. Useful as a placeholder for pipeline validation, dry-run
/// builds, and as the inner agent for testing custom <see cref="DelegatingAIAgent"/> decorators
/// without needing a full LLM round-trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Primary use case — construction-idempotency tests.</b> User-supplied
/// <see cref="DelegatingAIAgent"/> decorators registered via
/// <c>DurableAgentBuilder.ConfigureAgentPipeline</c> are constructed twice per agent registration
/// per worker process — once for startup validation (dry-run) and once on first activity dispatch.
/// Users testing their decorators against this contract can use <see cref="Instance"/> as the
/// inner agent for both constructions in a unit test:
/// </para>
/// <code>
/// new MyDecorator(NoOpAgent.Instance, deps);  // validation build
/// new MyDecorator(NoOpAgent.Instance, deps);  // real-use build
/// Assert.Equal(2, MyDecorator.ConstructionCount);
/// </code>
/// <para>
/// <b>Secondary use case — library-internal dry-run validation.</b> The C-check that runs at
/// worker startup (per Step 3b of the MAF gap-analysis plan) constructs an
/// <see cref="AIAgentBuilder"/> wrapping <see cref="Instance"/>, applies the user's configure
/// delegate, and calls <c>Build()</c> to materialize the chain so it can be walked for forbidden
/// middleware (e.g. <c>FunctionInvocationDelegatingAgent</c>). <see cref="Instance"/> is the
/// placeholder inner that lets that dry-run succeed without an LLM dependency.
/// </para>
/// <para>
/// <b>Why the singleton.</b> The placeholder has no state, no I/O, and identical behavior across
/// all callers. A static singleton avoids per-validation allocation and matches the contract that
/// the dry-run spike (Step 0) validated — see the spike outcome in
/// <c>artifacts/maf-gap-implementation-plan-v2.md</c>.
/// </para>
/// </remarks>
public sealed class NoOpAgent : AIAgent
{
    private static readonly NoOpAgent _instance = new();

    /// <summary>
    /// Gets the shared singleton instance. Prefer this over constructing new instances —
    /// <see cref="NoOpAgent"/> has no state and identical behavior across all callers.
    /// </summary>
    public static NoOpAgent Instance => _instance;

    /// <summary>Initializes a new instance of the <see cref="NoOpAgent"/> class.</summary>
    /// <remarks>
    /// Prefer <see cref="Instance"/> in production-shipped tests. The public constructor exists
    /// only so derived testing scenarios that need their own instance can opt out of the
    /// singleton (e.g., when asserting reference identity in tests).
    /// </remarks>
    public NoOpAgent()
    {
    }

    /// <inheritdoc/>
    public override string? Name => "NoOp";

    /// <inheritdoc/>
    public override string? Description => "Lightweight placeholder agent used for testing and dry-run validation.";

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default) =>
        new(new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("noop")));

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        new(JsonDocument.Parse("{}").RootElement);

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        new(new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("noop")));

    /// <inheritdoc/>
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentResponse());

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
