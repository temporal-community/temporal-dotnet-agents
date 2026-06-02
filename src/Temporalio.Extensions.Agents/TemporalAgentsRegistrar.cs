#pragma warning disable TA002 // built-in compaction strategies are experimental but registered by name here

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Agents.Compaction;
using Temporalio.Extensions.Agents.Internal;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.Agents;

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

        // Step 6c — pre-register the three built-in compaction strategies under their
        // canonical keys. TryAddKeyedSingleton makes user-supplied strategies registered
        // under the same key win (idempotent re-registration). The default-constructor
        // thresholds suit common conversational workloads; users wanting different
        // thresholds register their own keyed instance.
        services.TryAddKeyedSingleton<ICompactionStrategy, TruncationCompactionStrategy>(
            TruncationCompactionStrategy.Key);
        services.TryAddKeyedSingleton<ICompactionStrategy, SlidingWindowCompactionStrategy>(
            SlidingWindowCompactionStrategy.Key);
        services.TryAddKeyedSingleton<ICompactionStrategy, SummarizationCompactionStrategy>(
            SummarizationCompactionStrategy.Key);

        // Startup C-check: validates user-supplied ConfigureAgentPipeline callbacks against the
        // function-invocation conflict before any workflow can dispatch an activity. Skipped when
        // TemporalAgentsOptions.SkipDryRunCCheck = true. See artifacts/maf-gap-implementation-plan-v2.md
        // Step 3b for the design rationale.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAgentPipelineValidator>());
    }
}
