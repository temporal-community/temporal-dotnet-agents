using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Internal helper that performs the DI side of registering durable AI services.
/// Shared implementation for the co-hosted, client-only, and worker-only registration paths.
/// </summary>
internal static class DurableAIRegistrar
{
    /// <summary>
    /// Registers all durable AI services for a process that hosts both client and worker.
    /// </summary>
    public static void RegisterCoHosted(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder builder,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        if (options.RegisterDefaultWorkflow && !services.Any(d => d.ServiceType == typeof(ITemporalClient)))
        {
            throw new InvalidOperationException(
                "No ITemporalClient registered in DI. " +
                "Call services.AddTemporalClient(address, namespace) before calling AddDurableAI(). " +
                "For a worker-only process, use AddDurableAIWorker(); for a client-only process, " +
                "use AddDurableAIClient().");
        }

        RegisterSharedServices(services, options);
        RegisterWorkerServices(services, builder, options);
        if (options.RegisterDefaultWorkflow)
        {
            RegisterClientServices(services, options);
        }
    }

    /// <summary>
    /// Registers durable AI services for a client process that does not host a worker.
    /// </summary>
    public static void RegisterClient(IServiceCollection services, DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterSharedServices(services, options);
        RegisterClientServices(services, options);
    }

    /// <summary>
    /// Registers durable AI services for a worker process that does not start sessions.
    /// </summary>
    public static void RegisterWorker(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder builder,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        RegisterSharedServices(services, options);
        RegisterWorkerServices(services, builder, options);
    }

    public static bool IsWorkerRegistered(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder builder) =>
        FindWorkerRegistration(services, builder) is not null;

    private static void RegisterSharedServices(IServiceCollection services, DurableExecutionOptions options)
    {
        RegisterWorkflowInputServices(services, options);
    }

    private static void RegisterClientServices(IServiceCollection services, DurableExecutionOptions options)
    {
        services.TryAddSingleton<DurableChatSessionClient>(sp =>
        {
            var client = sp.GetService<ITemporalClient>()
                ?? throw new InvalidOperationException(
                    "No ITemporalClient registered in DI. Call services.AddTemporalClient(address, namespace) " +
                    "before resolving DurableChatSessionClient.");
            return new DurableChatSessionClient(
                client,
                options,
                sp.GetRequiredService<IDurableChatWorkflowInputFactory>(),
                sp.GetService<ILogger<DurableChatSessionClient>>());
        });
        services.TryAddSingleton<IDurableChatSessionClient>(
            sp => sp.GetRequiredService<DurableChatSessionClient>());
    }

    private static void RegisterWorkerServices(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder builder,
        DurableExecutionOptions options)
    {
        if (FindWorkerRegistration(services, builder) is not null)
        {
            return;
        }

        services.AddSingleton(new WorkerRegistrationMarker(builder));

        if (options.RegisterDefaultWorkflow)
        {
            builder.AddWorkflow<DurableChatWorkflow>();
        }

        builder.AddSingletonActivities<DurableChatActivities>();
        builder.AddScopedActivities<DurableFunctionActivities>();
        builder.AddSingletonActivities<DurableToolsetActivities>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAIWorkerClientConfigurator>());

        // A manually registered ITemporalClient bypasses the AddTemporalClient configurator.
        // Verify its converter at worker startup rather than allowing silent history data loss.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            Internal.DurableAIDataConverterValidator>());

        // Startup check for the MEAI mixed-pattern misconfiguration.
        // Detects DurableFunctionRegistry.Count > 0 (durable tools registered) +
        // FunctionInvokingChatClient in the IChatClient chain (.UseFunctionInvocation()
        // present). Both together = tool calls execute in-process inside the chat
        // activity, silently bypassing .AsDurable() dispatch. Fails the host at startup.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            Internal.DurableMixedPatternValidator>());

        // Default toolset IDs and cross-toolset visible-name collisions are knowable from the
        // completed worker service collection. Reject them while worker options are finalized,
        // rather than poisoning the first stock session with a deterministic resolver failure.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            Internal.DurableToolsetConfigurationValidator>());
    }

    private static WorkerRegistrationMarker? FindWorkerRegistration(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder builder) =>
        services
            .Where(d => d.ServiceType == typeof(WorkerRegistrationMarker))
            .Select(d => d.ImplementationInstance as WorkerRegistrationMarker)
            .FirstOrDefault(marker => marker is not null && ReferenceEquals(marker.Builder, builder));

    private sealed class WorkerRegistrationMarker(ITemporalWorkerServiceOptionsBuilder builder)
    {
        public ITemporalWorkerServiceOptionsBuilder Builder { get; } = builder;
    }

    /// <summary>
    /// Registers the replay-frozen workflow-input services without registering a worker, client,
    /// workflow, or activities. Used by declaration-only workflow starters.
    /// </summary>
    internal static void RegisterWorkflowInputServices(
        IServiceCollection services,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterClientDataConverterServices(services);

        services.TryAddSingleton(options);
        services.TryAddSingleton<DurableFunctionRegistry>();
        services.TryAddSingleton<Internal.DurableFunctionDeclarationRegistry>(sp =>
            new Internal.DurableFunctionDeclarationRegistry(
                sp.GetServices<Action<Internal.DurableFunctionDeclarationRegistry>>()));
        services.TryAddSingleton<Internal.DurableToolFactoryRegistry>(sp =>
            new Internal.DurableToolFactoryRegistry(
                sp.GetServices<Action<Internal.DurableToolFactoryRegistry>>()));
        services.TryAddSingleton<DurableChatToolOptionsRegistry>(sp =>
            new DurableChatToolOptionsRegistry(
                sp.GetServices<Action<DurableChatToolOptionsRegistry>>()));
        services.TryAddSingleton<Internal.DurableToolsetCatalog>(sp =>
            new Internal.DurableToolsetCatalog(
                sp.GetServices<DurableToolsetRegistration>(),
                options));
        services.TryAddSingleton<Internal.DurableToolsetActivationCatalog>(sp =>
            new Internal.DurableToolsetActivationCatalog(
                sp.GetServices<DurableToolsetRegistration>()));
        services.TryAddSingleton<IReadOnlyDictionary<string, AIFunction>>(
            sp => sp.GetRequiredService<DurableFunctionRegistry>());
        services.TryAddSingleton<IDurableChatWorkflowInputFactory>(sp =>
            new DurableChatWorkflowInputFactory(
                options,
                sp.GetService<DurableFunctionRegistry>(),
                sp.GetService<DurableChatToolOptionsRegistry>(),
                sp.GetService<Internal.DurableFunctionDeclarationRegistry>(),
                sp.GetServices<DurableToolsetRegistration>()));
    }

    private static void RegisterClientDataConverterServices(IServiceCollection services)
    {
        // Applies to clients created through AddTemporalClient. TryAddEnumerable makes the
        // registration order-independent and deduplicates full plus client-only registration.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            DurableAIClientOptionsConfigurator>());
    }
}
