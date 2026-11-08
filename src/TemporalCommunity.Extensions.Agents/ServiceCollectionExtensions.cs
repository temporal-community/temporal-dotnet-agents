using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Linq;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.Workflows;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Agent-specific extension methods for <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers client-side Temporal agent proxies for the specified agent names.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="taskQueue">The Temporal task queue that hosts the agents.</param>
    /// <param name="agentNames">The case-insensitive names of the remote agents.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddTemporalAgentProxies(
        this IServiceCollection services,
        string taskQueue,
        params string[] agentNames)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);
        ArgumentNullException.ThrowIfNull(agentNames);
        if (agentNames.Length == 0)
        {
            throw new ArgumentException("At least one Temporal agent name is required.", nameof(agentNames));
        }

        return services.AddTemporalAgentProxies(
            options =>
            {
                foreach (var name in agentNames)
                {
                    options.AddAgentProxy(name);
                }
            },
            taskQueue);
    }

    /// <summary>
    /// Gets a registered Temporal agent proxy by name.
    /// </summary>
    public static AIAgent GetTemporalAgentProxy(this IServiceProvider services, string name)
    {
        return services.GetKeyedService<AIAgent>(name)
            ?? throw new AgentNotRegisteredException(name);
    }

    /// <summary>
    /// Registers client-side Temporal Agent infrastructure only: an <see cref="ITemporalAgentClient"/>
    /// and keyed <see cref="AIAgent"/> proxy singletons. No Temporal worker is registered.
    /// </summary>
    /// <remarks>
    /// Use this in processes that only send messages to agent sessions (e.g. an API server, CLI tool)
    /// when the Temporal worker runs in a separate process. Declare the agents you want proxies for
    /// using <see cref="TemporalAgentsOptions.AddAgentProxy(string, TimeSpan?)"/> inside the
    /// <paramref name="configure"/> delegate.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Delegate to declare proxy agent names and optional TTLs.</param>
    /// <param name="taskQueue">The Temporal task queue that the worker is listening on.</param>
    /// <param name="targetHost">Optional Temporal server address (e.g. "localhost:7233").
    /// When provided, an <see cref="ITemporalClient"/> is registered.</param>
    /// <param name="namespace">Optional Temporal namespace. Defaults to "default".</param>
    public static IServiceCollection AddTemporalAgentProxies(
        this IServiceCollection services,
        Action<TemporalAgentsOptions> configure,
        string taskQueue,
        string? targetHost = null,
        string @namespace = "default")
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);

        var options = new TemporalAgentsOptions();
        configure(options);
        ValidateNoDuplicateAgentKeys(services, options.GetRegisteredAgentNames());

        // Options are used by DefaultTemporalAgentClient for TTL resolution when starting sessions.
        services.AddSingleton(options);

        if (targetHost is not null)
        {
            services.AddTemporalClient(targetHost, @namespace ?? "default");
        }

        // Auto-wire TemporalAgentDataConverter so that AgentResponse ChatMessage/AIContent
        // subtypes (e.g. TextContent) AND the MAF-specific session-entry subclasses round-trip
        // correctly. Without this, the client process uses DataConverter.Default which drops
        // $type discriminators, causing messages to deserialize as base types with no payload.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            TemporalAgentClientOptionsConfigurator>());

        services.AddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                options,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>()));

        // Register a keyed proxy for every declared agent name.
        // The real agent implementation lives in the worker process — no factory needed here.
        foreach (var name in options.GetRegisteredAgentNames())
        {
            var captured = name;
            services.AddKeyedSingleton<AIAgent>(captured, (sp, _) =>
                new TemporalAIAgentProxy(
                    captured,
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    sp.GetService<ILogger<TemporalAIAgentProxy>>()));
        }

        return services;
    }

    /// <summary>Validates that the named agent is registered.</summary>
    internal static void ValidateAgentIsRegistered(IServiceProvider services, string agentName)
    {
        var options = services.GetService<TemporalAgentsOptions>()
            ?? throw new InvalidOperationException(
                "Temporal agents have not been configured. Call AddTemporalAgents() on the worker builder first.");

        if (!options.IsAgentRegistered(agentName))
        {
            throw new AgentNotRegisteredException(agentName);
        }
    }

    internal static void ValidateNoDuplicateAgentKeys(
        IServiceCollection services,
        IEnumerable<string> agentNames)
    {
        var existingKeys = services
            .Where(descriptor => descriptor.ServiceType == typeof(AIAgent) && descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ServiceKey)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in agentNames)
        {
            if (existingKeys.Contains(name))
            {
                throw new InvalidOperationException(
                    $"A keyed AIAgent with key '{name}' is already registered. Temporal agent proxy keys must be unique.");
            }
        }
    }
}
