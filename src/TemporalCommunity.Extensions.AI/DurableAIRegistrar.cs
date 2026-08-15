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
/// Shared by <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/> and
/// the <c>DurableAIPlugin</c> entry point so the two paths converge on
/// byte-equivalent DI state. Idempotent — safe to call more than once thanks to
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
/// and <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>.
/// </summary>
internal static class DurableAIRegistrar
{
    /// <summary>
    /// Performs DI registration for durable AI: options, registry, session client,
    /// activities, default workflow, and DurableAIDataConverter auto-wiring.
    /// </summary>
    /// <param name="services">The service collection (always required).</param>
    /// <param name="builder">The worker options builder. When non-null, the
    /// default workflow and activities are registered onto the worker. When null
    /// (e.g., the plugin path that does not have a builder handy at registration
    /// time), only the DI-side registrations are applied.</param>
    /// <param name="options">The configured <see cref="DurableExecutionOptions"/>.</param>
    public static void Register(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder? builder,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterWorkflowInputServices(services, options);

        // Register the session client and default workflow only if enabled.
        if (options.RegisterDefaultWorkflow)
        {
            // The stock session client starts workflows through a DI client. An activity-only
            // worker can instead use AddHostedTemporalWorker(address, namespace, queue), whose
            // worker-owned client is not registered as ITemporalClient.
            if (!services.Any(d => d.ServiceType == typeof(ITemporalClient)))
            {
                throw new InvalidOperationException(
                    "No ITemporalClient registered in DI. " +
                    "Call services.AddTemporalClient(address, namespace) before calling AddDurableAI(). " +
                    "Note: AddHostedTemporalWorker(address, namespace, queue) stores connection settings " +
                    "on the worker service but does not register ITemporalClient in DI — " +
                    "AddTemporalClient is required separately when RegisterDefaultWorkflow is true.");
            }

            // Register the session client (concrete + interface alias share the same instance).
            // Inject both registries so the client can build durable-tool ActivityOptions at
            // session start when durable tools are present.
            services.TryAddSingleton<DurableChatSessionClient>(sp =>
                new DurableChatSessionClient(
                    sp.GetRequiredService<ITemporalClient>(),
                    options,
                    sp.GetRequiredService<IDurableChatWorkflowInputFactory>(),
                    sp.GetService<ILogger<DurableChatSessionClient>>()));
            services.TryAddSingleton<IDurableChatSessionClient>(
                sp => sp.GetRequiredService<DurableChatSessionClient>());

            // Register the default workflow on the worker, if a builder is available.
            builder?.AddWorkflow<DurableChatWorkflow>();
        }

        // Register activities on the worker (always needed) when a builder is available.
        if (builder is not null)
        {
            builder.AddSingletonActivities<DurableChatActivities>();
            builder.AddScopedActivities<DurableFunctionActivities>();
            builder.AddSingletonActivities<DurableEmbeddingActivities>();
            builder.AddSingletonActivities<DurableToolsetActivities>();
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAIWorkerClientConfigurator>());

        // Startup check for the MEAI mixed-pattern misconfiguration.
        // Detects DurableFunctionRegistry.Count > 0 (durable tools registered) +
        // FunctionInvokingChatClient in the IChatClient chain (.UseFunctionInvocation()
        // present). Both together = tool calls execute in-process inside the chat
        // activity, silently bypassing .AsDurable() dispatch. Fails the host at startup.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            Internal.DurableMixedPatternValidator>());
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
