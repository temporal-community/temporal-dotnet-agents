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

        // Fail fast if no ITemporalClient is registered. The DurableChatSessionClient
        // factory (below) calls GetRequiredService<ITemporalClient>() at resolution time;
        // without this check, a missing registration produces a cryptic generic
        // InvalidOperationException instead of an actionable message.
        //
        // Typical root cause: using the 1-arg AddHostedTemporalWorker(taskQueue) without
        // also calling services.AddTemporalClient(address, namespace). The 3-arg
        // AddHostedTemporalWorker(address, namespace, queue) stores connection settings on
        // TemporalWorkerServiceOptions.ClientOptions so the worker creates its own client at
        // startup — it intentionally does NOT register ITemporalClient in DI. Call
        // services.AddTemporalClient(address, namespace) before calling AddDurableAI() to
        // make ITemporalClient available as a DI service.
        if (!services.Any(d => d.ServiceType == typeof(ITemporalClient)))
        {
            throw new InvalidOperationException(
                "No ITemporalClient registered in DI. " +
                "Call services.AddTemporalClient(address, namespace) before calling AddDurableAI(). " +
                "Note: AddHostedTemporalWorker(address, namespace, queue) stores connection settings " +
                "on the worker service but does not register ITemporalClient in DI — " +
                "AddTemporalClient is required separately.");
        }

        // Register options as singleton.
        services.TryAddSingleton(options);

        // Register the function registry (populated by AddDurableTools calls).
        services.TryAddSingleton<DurableFunctionRegistry>();

        // Register the per-tool options registry (populated by AddDurableTools calls).
        // Every tool registered via AddDurableTools has an entry here, even if the caller
        // didn't supply an explicit configure callback — this guarantees the session client
        // sees a complete picture when it builds the workflow's ToolActivityOptions dict.
        // Explicit factory: the only ctor is internal and takes the configurators, so the
        // default DI activator (which needs a public ctor) cannot construct it. A factory
        // lambda in this assembly can invoke the internal ctor with the resolved configurators.
        services.TryAddSingleton<DurableChatToolOptionsRegistry>(sp =>
            new DurableChatToolOptionsRegistry(sp.GetServices<Action<DurableChatToolOptionsRegistry>>()));

        // Register the function registry as IReadOnlyDictionary for activity resolution.
        services.TryAddSingleton<IReadOnlyDictionary<string, AIFunction>>(
            sp => sp.GetRequiredService<DurableFunctionRegistry>());

        // Register the session client and default workflow only if enabled.
        if (options.RegisterDefaultWorkflow)
        {
            // Register the session client (concrete + interface alias share the same instance).
            // Inject both registries so the client can build Pattern 3 ToolActivityOptions at
            // session start when durable tools are present.
            services.TryAddSingleton<DurableChatSessionClient>(sp =>
                new DurableChatSessionClient(
                    sp.GetRequiredService<ITemporalClient>(),
                    options,
                    sp.GetService<ILogger<DurableChatSessionClient>>(),
                    sp.GetService<DurableFunctionRegistry>(),
                    sp.GetService<DurableChatToolOptionsRegistry>()));
            services.TryAddSingleton<IDurableChatSessionClient>(
                sp => sp.GetRequiredService<DurableChatSessionClient>());

            // Register the default workflow on the worker, if a builder is available.
            builder?.AddWorkflow<DurableChatWorkflow>();
        }

        // Register activities on the worker (always needed) when a builder is available.
        if (builder is not null)
        {
            builder.AddSingletonActivities<DurableChatActivities>();
            builder.AddSingletonActivities<DurableFunctionActivities>();
            builder.AddSingletonActivities<DurableEmbeddingActivities>();
        }

        // Pre-register the built-in "tags" IChatClientDecorator (Step 4b of the maf-gap plan).
        // Per Q-ChatClientFactory-shape, this is the "80% case" path — users can call
        // WithChatClientTag(name, value) + WithChatClientFactoryKey("tags") without registering
        // a custom decorator. TryAddKeyedSingleton makes this idempotent across both registration
        // paths (AddDurableAI directly + transitively via AddTemporalAgents).
        services.TryAddKeyedSingleton<IChatClientDecorator>(
            "tags",
            (sp, _) => new Internal.TagsChatClientDecorator(sp.GetService<ILoggerFactory>()));

        // Auto-wire DurableAIDataConverter for both client registration patterns.
        // TryAddEnumerable deduplicates if registration happens more than once.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            DurableAIClientOptionsConfigurator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAIWorkerClientConfigurator>());

        // Step 4d: A-check for the silent MEAI mixed-pattern misconfiguration.
        // Detects DurableFunctionRegistry.Count > 0 (durable tools registered) +
        // FunctionInvokingChatClient in the IChatClient chain (.UseFunctionInvocation()
        // present). Both together = tool calls execute in-process inside the chat
        // activity, silently bypassing .AsDurable() dispatch. Fails the host at startup.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            Internal.DurableMixedPatternValidator>());
    }
}
