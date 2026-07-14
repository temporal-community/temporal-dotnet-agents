using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.Internal;
using TemporalCommunity.Extensions.Agents.Scheduling;
using TemporalCommunity.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Internal helper that performs the DI side of registering agent services.
/// Shared by <see cref="TemporalWorkerBuilderExtensions.AddTemporalAgents"/> and
/// <see cref="TemporalAgentsPlugin"/> so both paths converge on identical DI state.
/// </summary>
internal static class TemporalAgentsRegistrar
{
    /// <summary>
    /// Performs DI registration for temporal agents: options, agent client, keyed proxies,
    /// workflow, activities, and DurableAIDataConverter auto-wiring.
    /// </summary>
    public static void Register(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder? builder,
        TemporalAgentsOptions agentsOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(agentsOptions);

        // Fail fast if no ITemporalClient is registered. The DefaultTemporalAgentClient
        // factory (below) calls GetRequiredService<ITemporalClient>() at resolution time;
        // without this check, a missing registration produces a cryptic generic
        // InvalidOperationException instead of an actionable message.
        //
        // Typical root cause: using the 1-arg AddHostedTemporalWorker(taskQueue) without
        // also calling services.AddTemporalClient(address, namespace). The 3-arg
        // AddHostedTemporalWorker(address, namespace, queue) stores connection settings on
        // TemporalWorkerServiceOptions.ClientOptions so the worker creates its own client at
        // startup — it intentionally does NOT register ITemporalClient in DI. Call
        // services.AddTemporalClient(address, namespace) before calling AddTemporalAgents() to
        // make ITemporalClient available as a DI service.
        if (!services.Any(d => d.ServiceType == typeof(ITemporalClient)))
        {
            throw new InvalidOperationException(
                "No ITemporalClient registered in DI. " +
                "Call services.AddTemporalClient(address, namespace) before calling AddTemporalAgents(). " +
                "Note: AddHostedTemporalWorker(address, namespace, queue) stores connection settings " +
                "on the worker service but does not register ITemporalClient in DI — " +
                "AddTemporalClient is required separately.");
        }

        var taskQueue = builder?.TaskQueue ?? string.Empty;

        // Options singleton — consumed by DefaultTemporalAgentClient and AgentActivities.
        services.TryAddSingleton(agentsOptions);

        // ITemporalAgentClient — uses WorkflowUpdate for synchronous request/response semantics.
        // TryAddSingleton allows callers to pre-register a custom implementation (e.g. a test double).
        services.TryAddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                agentsOptions,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>()));

        // Register a keyed AIAgent proxy singleton for every declared agent name (durable + proxy).
        foreach (var name in agentsOptions.GetRegisteredAgentNames())
        {
            var agentName = name;
            services.AddKeyedSingleton<AIAgent>(agentName, (sp, _) =>
                new TemporalAIAgentProxy(
                    agentName,
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    sp.GetService<ILogger<TemporalAIAgentProxy>>()));
        }

        if (builder is not null)
        {
            // Register the durable session workflow and activity implementations on this worker.
            builder.AddWorkflow<AgentWorkflow>();
            builder.AddSingletonActivities<AgentActivities>();

            // AgentJobWorkflow: simple fire-and-forget workflow for scheduled/deferred runs.
            builder.AddWorkflow<AgentJobWorkflow>();

            // ScheduleActivities: enables one-time deferred runs from orchestrating workflows.
            services.AddSingleton(sp => new ScheduleActivities(
                sp.GetRequiredService<ITemporalClient>(),
                taskQueue,
                agentsOptions));
            builder.AddSingletonActivities<ScheduleActivities>();

            // ScheduleRegistrationService: creates configured schedules at worker startup.
            if (agentsOptions.GetScheduledRuns().Count > 0)
            {
                services.AddHostedService<ScheduleRegistrationService>(sp =>
                    new ScheduleRegistrationService(
                        sp.GetRequiredService<ITemporalAgentClient>(),
                        agentsOptions,
                        sp.GetService<ILogger<ScheduleRegistrationService>>()));
            }
        }

        // Auto-wire TemporalAgentDataConverter. Mirrors what AddDurableAI() does for the AI
        // library — except the MAF converter is a strict superset that also handles the
        // agent-specific session-entry subclasses (AgentSessionRequest / AgentSessionResponse).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            TemporalAgentClientOptionsConfigurator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            TemporalAgentWorkerClientConfigurator>());

        // Startup C-check: validates user-supplied ConfigureAgentPipeline callbacks against the
        // function-invocation conflict before any workflow can dispatch an activity. Skipped when
        // TemporalAgentsOptions.SkipDryRunCCheck = true.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAgentPipelineValidator>());
    }
}
