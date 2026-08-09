using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.Agents.Internal;

/// <summary>Builds and owns one configured MAF agent pipeline.</summary>
internal static class AgentPipelineComposer
{
    /// <summary>
    /// Builds a pipeline around <paramref name="innerAgent"/>, verifies its topology, and records
    /// the package-owned middleware instances that must be disposed with the pipeline.
    /// </summary>
    internal static AgentPipelineLease Compose(
        string agentName,
        AIAgent innerAgent,
        Action<AIAgentBuilder>? configurePipeline,
        IServiceProvider services,
        Action<AIAgentBuilder>? appendInnermostPipeline = null,
        IChatClient? chatClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(innerAgent);
        ArgumentNullException.ThrowIfNull(services);

        AIAgent builtAgent = innerAgent;
        if (configurePipeline is not null || appendInnermostPipeline is not null)
        {
            var builder = new AIAgentBuilder(innerAgent);
            configurePipeline?.Invoke(builder);
            // AIAgentBuilder applies factories in reverse. Appending library middleware after
            // user middleware therefore places it immediately above the supplied leaf.
            appendInnermostPipeline?.Invoke(builder);
            builtAgent = builder.Build(services);
        }

        var pipelineLinks = AgentChainWalker.WalkAIAgent(builtAgent).ToList();
        var ownedOpenTelemetryAgents = new List<OpenTelemetryAgent>();
        try
        {
            // Discover package-owned disposable middleware before any validation can reject the
            // successfully built chain. This makes every post-Build failure cleanup-safe.
            foreach (var link in pipelineLinks)
            {
                if (link is OpenTelemetryAgent openTelemetryAgent
                    && !ownedOpenTelemetryAgents.Any(owned =>
                        ReferenceEquals(owned, openTelemetryAgent)))
                {
                    ownedOpenTelemetryAgents.Add(openTelemetryAgent);
                }
            }

            DurableAgentPipelineTopology.EnsurePreservesInnerAgent(
                agentName,
                builtAgent,
                innerAgent);

            foreach (var link in pipelineLinks)
            {
                if (ReferenceEquals(link, innerAgent))
                {
                    continue;
                }

                if (link is OpenTelemetryAgent openTelemetryAgent)
                {
                    continue;
                }

                if (link is IDisposable || link is IAsyncDisposable)
                {
                    throw new DurableConfigurationException(
                        $"Agent '{agentName}' has custom middleware '{link.GetType().FullName}' " +
                        "that implements IDisposable or IAsyncDisposable. Its ownership cannot be " +
                        "determined safely. Custom ConfigureAgentPipeline wrappers must not own " +
                        "disposable resources; resolve those resources from the activity DI scope " +
                        "instead. OpenTelemetryAgent is the only disposable wrapper owned and " +
                        "disposed by this library.");
                }
            }

            return new AgentPipelineLease(
                builtAgent,
                ownedOpenTelemetryAgents,
                AgentChainWalker.Contains<OpenTelemetryChatClient>(chatClient));
        }
        catch
        {
            DisposeOwnedAgents(ownedOpenTelemetryAgents);
            throw;
        }
    }

    internal static void DisposeOwnedAgents(IReadOnlyList<OpenTelemetryAgent> agents)
    {
        for (var i = agents.Count - 1; i >= 0; i--)
        {
            agents[i].Dispose();
        }
    }
}

/// <summary>Lifetime boundary for a single composed MAF agent pipeline.</summary>
internal sealed class AgentPipelineLease : IDisposable
{
    private readonly IReadOnlyList<OpenTelemetryAgent> _ownedOpenTelemetryAgents;
    private bool _disposed;

    internal AgentPipelineLease(
        AIAgent agent,
        IReadOnlyList<OpenTelemetryAgent> ownedOpenTelemetryAgents,
        bool hasOpenTelemetryChatClient)
    {
        Agent = agent;
        _ownedOpenTelemetryAgents = ownedOpenTelemetryAgents;
        HasOpenTelemetryChatClient = hasOpenTelemetryChatClient;
    }

    internal AIAgent Agent { get; }

    internal bool HasOpenTelemetryAgent => _ownedOpenTelemetryAgents.Count > 0;

    internal bool HasOpenTelemetryChatClient { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AgentPipelineComposer.DisposeOwnedAgents(_ownedOpenTelemetryAgents);
    }
}
