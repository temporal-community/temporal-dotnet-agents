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

        var options = new TemporalAgentsOptions();
        foreach (var name in agentNames)
        {
            options.AddAgentProxy(name);
        }

        ValidateNoDuplicateAgentKeys(services, options.GetRegisteredAgentNames());
        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            TemporalAgentClientOptionsConfigurator>());
        services.AddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                options,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>()));

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

    /// <summary>
    /// Gets a registered Temporal agent proxy by name.
    /// </summary>
    public static AIAgent GetTemporalAgentProxy(this IServiceProvider services, string name)
    {
        return services.GetKeyedService<AIAgent>(name)
            ?? throw new AgentNotRegisteredException(name);
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
