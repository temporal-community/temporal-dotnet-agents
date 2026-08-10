using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Extension methods for registering durable AI services.
/// </summary>
public static class DurableAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers only the services needed to create replay-frozen workflow input in a process
    /// that starts workflows but does not host a Temporal worker.
    /// </summary>
    /// <param name="services">The workflow-starting process service collection.</param>
    /// <param name="taskQueue">The task queue used by the implementation-bearing worker.</param>
    /// <param name="configure">Optional durable execution configuration.</param>
    /// <returns>The same service collection for declaration registration.</returns>
    /// <remarks>
    /// Follow this call with the <see cref="AddDurableToolDeclaration(IServiceCollection,AIFunctionDeclaration,Action{DurableChatToolOptions}?)"/>
    /// overload, then resolve <see cref="IDurableChatWorkflowInputFactory"/> outside workflow code.
    /// Clients created through <c>AddTemporalClient</c> are configured to use
    /// <see cref="DurableAIDataConverter.Instance"/> when their converter is still the SDK default.
    /// A custom converter is preserved. Manually constructed clients must set the durable converter
    /// explicitly. This method does not register workflows, activities, worker configuration, or
    /// <see cref="DurableChatSessionClient"/>.
    /// </remarks>
    public static IServiceCollection AddDurableChatWorkflowInputFactory(
        this IServiceCollection services,
        string taskQueue,
        Action<DurableExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);

        var options = new DurableExecutionOptions
        {
            TaskQueue = taskQueue,
            RegisterDefaultWorkflow = false,
        };
        configure?.Invoke(options);

        // These two values define this deliberately worker-free registration path and cannot be
        // changed by the optional callback.
        options.TaskQueue = taskQueue;
        options.RegisterDefaultWorkflow = false;
        options.Validate();

        DurableAIRegistrar.RegisterWorkflowInputServices(services, options);
        return services;
    }

    /// <summary>
    /// Registers the durable AI workflow, activities, and support services on a Temporal worker.
    /// </summary>
    /// <param name="builder">The worker options builder returned by AddHostedTemporalWorker.</param>
    /// <param name="configure">Optional delegate to configure <see cref="DurableExecutionOptions"/>.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// Before calling this method, register an <see cref="IChatClient"/> in the service collection.
    /// Register the chat client without inline function-invocation middleware:
    /// </para>
    /// <code>
    /// builder.Services
    ///     .AddChatClient(innerClient);
    /// </code>
    /// <para>
    /// <see cref="DurableChatActivities"/> constructor-injects the <b>unkeyed</b> <see cref="IChatClient"/>.
    /// If using <c>AddKeyedChatClient</c> for multiple clients, also register an unkeyed alias.
    /// </para>
    /// <para>
    /// <see cref="DurableAIDataConverter"/> is automatically applied to the Temporal client when
    /// using <c>AddTemporalClient(address, ns)</c> or the 3-arg <c>AddHostedTemporalWorker(address, ns, queue)</c>
    /// overload that creates its own client. When creating the client manually via
    /// <c>TemporalClient.ConnectAsync</c> and registering it with <c>AddSingleton</c>, you must
    /// still set <c>DataConverter = DurableAIDataConverter.Instance</c> explicitly.
    /// </para>
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableAI(
        this ITemporalWorkerServiceOptionsBuilder builder,
        Action<DurableExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new DurableExecutionOptions
        {
            TaskQueue = builder.TaskQueue
        };
        configure?.Invoke(options);
        options.Validate();

        DurableAIRegistrar.Register(builder.Services, builder, options);

        return builder;
    }

    /// <summary>
    /// Registers one or more <see cref="AIFunction"/> tools for durable execution.
    /// Each tool can be resolved by name inside <see cref="DurableFunctionActivities"/>
    /// when invoked via <see cref="DurableAIFunctionExtensions.AsDurable"/> inside a workflow,
    /// or dispatched automatically by <see cref="DurableChatWorkflow"/> in a managed session.
    /// </summary>
    /// <param name="builder">The worker options builder returned by <see cref="AddDurableAI"/>.</param>
    /// <param name="tools">The tools to register. Each receives a default
    /// <see cref="DurableChatToolOptions"/> entry. Use the single-tool overload to attach
    /// per-tool options.</param>
    /// <returns>The same builder for further chaining.</returns>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableTools(
        this ITemporalWorkerServiceOptionsBuilder builder,
        params AIFunction[] tools)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            AddDurableTools(builder, tool, configure: null);
        }

        return builder;
    }

    /// <summary>
    /// Registers a single <see cref="AIFunction"/> tool for durable execution with optional
    /// per-tool retry / timeout overrides applied when the tool is dispatched as a Temporal
    /// activity in a managed session.
    /// </summary>
    /// <param name="builder">The worker options builder returned by <see cref="AddDurableAI"/>.</param>
    /// <param name="tool">The tool to register.</param>
    /// <param name="configure">
    /// Optional callback that receives a <see cref="DurableChatToolOptions"/> instance for
    /// configuring per-tool <c>StartToCloseTimeout</c>, <c>HeartbeatTimeout</c>, or
    /// <c>RetryPolicy</c>. Pass <see langword="null"/> for defaults.
    /// </param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// Call this after <see cref="AddDurableAI"/>. Tools registered via this method always
    /// receive an entry in the per-tool options registry — a default
    /// <see cref="DurableChatToolOptions"/> when <paramref name="configure"/> is
    /// <see langword="null"/>, otherwise the configured instance. This guarantees the
    /// <see cref="DurableChatSessionClient"/> sees every registered tool when it resolves
    /// managed-session tool dispatch at session start.
    ///
    /// <para>
    /// Write-style tools (send email, persist a record, charge a card) should call
    /// <see cref="DurableChatToolOptions.NoRetry"/> to prevent double-execution on activity
    /// retry.
    /// </para>
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableTools(
        this ITemporalWorkerServiceOptionsBuilder builder,
        AIFunction tool,
        Action<DurableChatToolOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tool);

        if (!builder.Services.Any(d => d.ServiceType == typeof(DurableExecutionOptions)))
        {
            throw new InvalidOperationException(
                "AddDurableTools requires AddDurableAI to be called first on the same worker builder.");
        }

        var services = builder.Services;

        // Capture the registration as a Func<DurableChatToolOptions> so the registry can
        // materialize options lazily — this avoids running user-supplied configure delegates
        // at registration time and keeps DI state idempotent across plugin/extension paths.
        var perToolOptions = new DurableChatToolOptions();
        configure?.Invoke(perToolOptions);

        services.AddSingleton<Action<DurableFunctionRegistry>>(
            registry => registry.Register(tool));
        var declaration = DurableFunctionDeclarationSnapshot.Create(tool.AsDeclarationOnly());
        services.AddSingleton<Action<DurableFunctionDeclarationRegistry>>(
            registry => registry[declaration.Name] = declaration);
        services.AddSingleton<Action<DurableChatToolOptionsRegistry>>(
            registry => registry[tool.Name] = perToolOptions);

        return builder;
    }

    /// <summary>
    /// Registers a stable model-facing declaration and an activity-local implementation factory
    /// for a durable tool that needs typed request data or turn state.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once per tool activity attempt. Its service provider belongs to that
    /// attempt's DI scope and must not be captured beyond the returned function's invocation.
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableTool<TRequestData, TTurnState>(
        this ITemporalWorkerServiceOptionsBuilder builder,
        AIFunctionDeclaration declaration,
        Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory,
        Action<DurableChatToolOptions>? configure = null)
    {
        AddDurableToolDeclaration(builder, declaration, configure);
        AddDurableToolImplementation(builder, declaration.Name, factory);
        return builder;
    }

    /// <summary>
    /// Registers only a stable model-facing declaration. Use this in a client process that starts
    /// workflows but does not host executable tool implementations.
    /// </summary>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolDeclaration(
        this ITemporalWorkerServiceOptionsBuilder builder,
        AIFunctionDeclaration declaration,
        Action<DurableChatToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);
        EnsureDurableAIRegistered(builder);

        var snapshot = DurableFunctionDeclarationSnapshot.Create(declaration);
        var perToolOptions = new DurableChatToolOptions();
        configure?.Invoke(perToolOptions);

        builder.Services.AddSingleton<Action<DurableFunctionDeclarationRegistry>>(
            registry => registry[snapshot.Name] = snapshot);
        builder.Services.AddSingleton<Action<DurableChatToolOptionsRegistry>>(
            registry => registry[snapshot.Name] = perToolOptions);
        return builder;
    }

    /// <summary>
    /// Registers a stable model-facing declaration in a workflow-starting process that does not
    /// host a Temporal worker.
    /// </summary>
    /// <remarks>
    /// Call <see cref="AddDurableChatWorkflowInputFactory(IServiceCollection,string,Action{DurableExecutionOptions}?)"/>
    /// first. The implementation-bearing worker registers the matching named factory with
    /// <see cref="AddDurableToolImplementation{TRequestData,TTurnState}"/>.
    /// </remarks>
    public static IServiceCollection AddDurableToolDeclaration(
        this IServiceCollection services,
        AIFunctionDeclaration declaration,
        Action<DurableChatToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(declaration);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(DurableExecutionOptions)))
        {
            throw new InvalidOperationException(
                "AddDurableToolDeclaration requires AddDurableChatWorkflowInputFactory to be called first.");
        }

        var snapshot = DurableFunctionDeclarationSnapshot.Create(declaration);
        var perToolOptions = new DurableChatToolOptions();
        configure?.Invoke(perToolOptions);
        services.AddSingleton<Action<DurableFunctionDeclarationRegistry>>(
            registry => registry[snapshot.Name] = snapshot);
        services.AddSingleton<Action<DurableChatToolOptionsRegistry>>(
            registry => registry[snapshot.Name] = perToolOptions);
        return services;
    }

    /// <summary>
    /// Registers only the invocation-scoped implementation factory for a named declaration. Use
    /// this in an implementation-bearing worker process.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once per tool activity attempt with that attempt's scoped service
    /// provider. Resolve scoped application services inside the factory rather than at startup.
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolImplementation<TRequestData, TTurnState>(
        this ITemporalWorkerServiceOptionsBuilder builder,
        string name,
        Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        EnsureDurableAIRegistered(builder);

        builder.Services.AddSingleton<Action<DurableToolFactoryRegistry>>(
            registry => registry[name] = new DurableToolActivationFactory<TRequestData, TTurnState>(factory));
        return builder;
    }

    private static void EnsureDurableAIRegistered(ITemporalWorkerServiceOptionsBuilder builder)
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(DurableExecutionOptions)))
        {
            throw new InvalidOperationException(
                "Durable tool registration requires AddDurableAI to be called first on the same worker builder.");
        }
    }
}

/// <summary>
/// Registry for <see cref="AIFunction"/> instances that can be invoked durably.
/// </summary>
// Note: do NOT re-declare `IReadOnlyDictionary<string, AIFunction>` here — `Dictionary<TKey, TValue>`
// already implements it, and explicitly re-declaring the interface triggers CS8644 ("nullability
// of reference types in interface implemented by the base type does not match") because the
// compiler tries to re-implement Values/Keys with annotations that differ from the base's.
// Consumers that want the interface (see DurableAIRegistrar.cs:52-53) still get it via the
// concrete-to-interface DI registration.
internal sealed class DurableFunctionRegistry : Dictionary<string, AIFunction>
{
    public DurableFunctionRegistry(IEnumerable<Action<DurableFunctionRegistry>>? configurators = null)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        if (configurators is null) return;

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }

    public void Register(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        this[function.Name] = function;
    }
}

/// <summary>
/// Registry of per-tool <see cref="DurableChatToolOptions"/> overrides. Populated by
/// <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction, System.Action{DurableChatToolOptions}?)"/>
/// — every registered tool gets an entry, even if the caller passed
/// <see langword="null"/> for <c>configure</c>. The <see cref="DurableChatSessionClient"/>
/// consumes this registry to build the per-tool <c>ActivityOptions</c> dictionary that
/// drives managed tool dispatch.
/// </summary>
// The same CS8644 caveat as DurableFunctionRegistry above — Dictionary<TKey, TValue> already
// implements IReadOnlyDictionary<TKey, TValue>; re-declaring it would trigger the nullability
// mismatch warning. Don't add the interface back to this class declaration.
internal sealed class DurableChatToolOptionsRegistry
    : Dictionary<string, DurableChatToolOptions>
{
    /// <summary>
    /// Initializes a new <see cref="DurableChatToolOptionsRegistry"/> by invoking each of
    /// the supplied configurators against the empty registry. Configurators are registered
    /// by <see cref="DurableAIServiceCollectionExtensions.AddDurableTools(global::Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, global::Microsoft.Extensions.AI.AIFunction, System.Action{DurableChatToolOptions}?)"/>
    /// and resolved as a service-collection enumerable.
    /// </summary>
    internal DurableChatToolOptionsRegistry(
        IEnumerable<Action<DurableChatToolOptionsRegistry>>? configurators = null)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        if (configurators is null) return;

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }
}
