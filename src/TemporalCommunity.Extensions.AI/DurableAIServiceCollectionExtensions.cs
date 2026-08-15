using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using TemporalCommunity.Extensions.AI.Internal;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Extension methods for registering durable AI services.
/// </summary>
public static class DurableAIServiceCollectionExtensions
{
    internal const string ImplicitDefaultToolsetId = "default";

    /// <summary>
    /// Registers only the services needed to create replay-frozen workflow input in a process
    /// that starts workflows but does not host a Temporal worker.
    /// </summary>
    /// <param name="services">The workflow-starting process service collection.</param>
    /// <param name="taskQueue">The task queue used by the implementation-bearing worker.</param>
    /// <param name="configure">Optional durable execution configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// In the preferred worker-owned toolset mode, the workflow-starting process registers no
    /// tool declarations; resolve <see cref="IDurableChatWorkflowInputFactory"/> outside workflow
    /// code and let the worker resolve its recorded baseline. Follow this call with
    /// <see cref="AddDurableToolDeclaration(IServiceCollection,AIFunctionDeclaration,Action{DurableChatToolOptions}?)"/>
    /// only for the advanced caller-owned declaration mode, where this process intentionally owns
    /// and freezes the model-facing schemas.
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
    /// <para>
    /// The converter applies to the worker's entire Temporal payload boundary, not just durable AI
    /// workflows. Ordinary workflows sharing that worker also write their inputs and results with the
    /// same converter. Every client that exchanges payloads with those workflows must use a compatible
    /// converter; a manually constructed client with the SDK default converter can otherwise materialize
    /// camel-case payloads as null or default application members without throwing.
    /// </para>
    /// <para>
    /// A DI <see cref="ITemporalClient"/> is required when <see cref="DurableExecutionOptions.RegisterDefaultWorkflow"/>
    /// is enabled because the stock session client starts workflows. An activity-only implementation
    /// worker may disable the default workflow and use the client owned by the three-argument
    /// <c>AddHostedTemporalWorker</c> overload without registering a second DI client.
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
    /// <see langword="null"/>, otherwise the configured instance. This guarantees the implicit
    /// worker-owned toolset has a complete policy entry for every member when its manifest is
    /// resolved. The advanced caller-owned mode uses the same registry to freeze per-tool policy.
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

        EnsureDurableAIRegistered(builder, nameof(AddDurableTools));
        var toolset = GetOrAddImplicitDefaultToolset(builder.Services);
        toolset.Add(RegisterDurableFunction(builder.Services, tool, configure));

        return builder;
    }

    /// <summary>
    /// Registers a named, worker-owned set of durable tools.
    /// </summary>
    /// <param name="builder">The worker options builder returned by <see cref="AddDurableAI"/>.</param>
    /// <param name="toolsetId">The stable, case-sensitive toolset identifier.</param>
    /// <param name="configure">Adds the ordered members of the toolset.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// Toolset identifiers and function names use exact ordinal comparison. A named toolset must
    /// contain at least one member. Implementations remain worker-local; a later resolver activity
    /// freezes only declarations and durable policy into workflow history.
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolset(
        this ITemporalWorkerServiceOptionsBuilder builder,
        string toolsetId,
        Action<DurableToolsetBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsetId);
        ArgumentNullException.ThrowIfNull(configure);
        EnsureDurableAIRegistered(builder, nameof(AddDurableToolset));

        if (FindToolset(builder.Services, toolsetId) is not null)
        {
            throw new InvalidOperationException(
                $"A durable toolset named '{toolsetId}' is already registered. " +
                "Toolset identifiers use exact ordinal comparison.");
        }

        var registration = new DurableToolsetRegistration(toolsetId, isImplicitDefault: false);
        var toolsetBuilder = new DurableToolsetBuilder(builder, registration);
        configure(toolsetBuilder);
        if (registration.FunctionNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"Durable toolset '{toolsetId}' must contain at least one tool.");
        }

        builder.Services.AddSingleton(registration);
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
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolFactory<TRequestData, TTurnState>(
        this ITemporalWorkerServiceOptionsBuilder builder,
        AIFunctionDeclaration declaration,
        Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory,
        Action<DurableChatToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(factory);
        EnsureDurableAIRegistered(builder);
        var toolset = GetOrAddImplicitDefaultToolset(builder.Services);
        toolset.Add(RegisterDurableToolFactory(builder, declaration, factory, configure));
        return builder;
    }

    /// <summary>
    /// Registers one explicitly selected instance method as a durable tool. The declaration and
    /// receiver activator are created once during registration; a fresh receiver is created and
    /// disposed for every tool activity attempt.
    /// </summary>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolFactory<THandler>(
        this ITemporalWorkerServiceOptionsBuilder builder,
        string methodName,
        AIFunctionFactoryOptions? functionOptions = null,
        Action<DurableChatToolOptions>? configure = null)
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        EnsureDurableAIRegistered(builder);
        var toolset = GetOrAddImplicitDefaultToolset(builder.Services);
        toolset.Add(RegisterMethodTool<THandler>(
            builder.Services,
            ResolveMethod<THandler>(methodName),
            functionOptions,
            configure));
        return builder;
    }

    /// <summary>
    /// Registers one explicitly selected instance method as a durable tool. This overload avoids
    /// ambiguity when the handler has overloaded methods.
    /// </summary>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableToolFactory<THandler>(
        this ITemporalWorkerServiceOptionsBuilder builder,
        MethodInfo method,
        AIFunctionFactoryOptions? functionOptions = null,
        Action<DurableChatToolOptions>? configure = null)
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(method);
        EnsureDurableAIRegistered(builder);
        ValidateMethod<THandler>(method);
        var toolset = GetOrAddImplicitDefaultToolset(builder.Services);
        toolset.Add(RegisterMethodTool<THandler>(builder.Services, method, functionOptions, configure));
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

    internal static DurableRegisteredTool RegisterDurableFunction(
        IServiceCollection services,
        AIFunction tool,
        Action<DurableChatToolOptions>? configure)
    {
        var perToolOptions = new DurableChatToolOptions();
        configure?.Invoke(perToolOptions);

        services.AddSingleton<Action<DurableFunctionRegistry>>(
            registry => registry.Register(tool));
        var declaration = DurableFunctionDeclarationSnapshot.Create(tool.AsDeclarationOnly());
        services.AddSingleton<Action<DurableFunctionDeclarationRegistry>>(
            registry => registry[declaration.Name] = declaration);
        services.AddSingleton<Action<DurableChatToolOptionsRegistry>>(
            registry => registry[tool.Name] = perToolOptions);
        return new DurableRegisteredTool(declaration, perToolOptions, tool, null);
    }

    internal static DurableRegisteredTool RegisterDurableToolFactory<TRequestData, TTurnState>(
        ITemporalWorkerServiceOptionsBuilder builder,
        AIFunctionDeclaration declaration,
        Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory,
        Action<DurableChatToolOptions>? configure)
    {
        var snapshot = DurableFunctionDeclarationSnapshot.Create(declaration);
        var perToolOptions = new DurableChatToolOptions();
        configure?.Invoke(perToolOptions);
        var activationFactory = new DurableToolActivationFactory<TRequestData, TTurnState>(factory);

        builder.Services.AddSingleton<Action<DurableFunctionDeclarationRegistry>>(
            registry => registry[snapshot.Name] = snapshot);
        builder.Services.AddSingleton<Action<DurableChatToolOptionsRegistry>>(
            registry => registry[snapshot.Name] = perToolOptions);
        builder.Services.AddSingleton<Action<DurableToolFactoryRegistry>>(
            registry => registry[snapshot.Name] = activationFactory);
        return new DurableRegisteredTool(snapshot, perToolOptions, null, activationFactory);
    }

    internal static DurableRegisteredTool RegisterMethodTool<THandler>(
        IServiceCollection services,
        MethodInfo method,
        AIFunctionFactoryOptions? functionOptions,
        Action<DurableChatToolOptions>? configure)
        where THandler : class
    {
        ValidateMethod<THandler>(method);
        var activator = ActivatorUtilities.CreateFactory(typeof(THandler), Type.EmptyTypes);
        var function = AIFunctionFactory.Create(
            method,
            arguments => activator(
                arguments.Services
                    ?? throw new InvalidOperationException(
                        $"Durable tool '{method.Name}' requires an activity service provider."),
                null),
            functionOptions ?? new AIFunctionFactoryOptions());
        return RegisterDurableFunction(services, function, configure);
    }

    internal static MethodInfo ResolveMethod<THandler>(string methodName)
        where THandler : class
    {
        var matches = typeof(THandler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException(
                $"Public instance method '{methodName}' was not found on '{typeof(THandler).FullName}'.",
                nameof(methodName)),
            _ => throw new ArgumentException(
                $"Method name '{methodName}' is overloaded on '{typeof(THandler).FullName}'. " +
                "Use the MethodInfo overload to select one method explicitly.",
                nameof(methodName)),
        };
    }

    internal static void ValidateMethod<THandler>(MethodInfo method)
        where THandler : class
    {
        if (method.IsStatic
            || !method.IsPublic
            || method.ContainsGenericParameters
            || method.DeclaringType is null
            || !method.DeclaringType.IsAssignableFrom(typeof(THandler)))
        {
            throw new ArgumentException(
                $"Method '{method.Name}' must be a closed public instance method callable on " +
                $"'{typeof(THandler).FullName}'.",
                nameof(method));
        }
    }

    private static DurableToolsetRegistration GetOrAddImplicitDefaultToolset(
        IServiceCollection services)
    {
        var existing = FindToolset(services, ImplicitDefaultToolsetId);
        if (existing is not null)
        {
            if (!existing.IsImplicitDefault)
            {
                throw new InvalidOperationException(
                    $"The reserved implicit toolset identifier '{ImplicitDefaultToolsetId}' " +
                    "cannot also be registered explicitly.");
            }

            return existing;
        }

        var registration = new DurableToolsetRegistration(
            ImplicitDefaultToolsetId,
            isImplicitDefault: true);
        services.AddSingleton(registration);
        return registration;
    }

    private static DurableToolsetRegistration? FindToolset(
        IServiceCollection services,
        string toolsetId) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(DurableToolsetRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as DurableToolsetRegistration)
            .FirstOrDefault(registration => registration is not null
                && string.Equals(registration.Id, toolsetId, StringComparison.Ordinal));

    private static void EnsureDurableAIRegistered(
        ITemporalWorkerServiceOptionsBuilder builder,
        string? caller = null)
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(DurableExecutionOptions)))
        {
            throw new InvalidOperationException(
                $"{caller ?? "Durable tool registration"} requires AddDurableAI to be called first " +
                "on the same worker builder.");
        }
    }
}

internal sealed class DurableToolsetRegistration(string id, bool isImplicitDefault)
{
    private readonly List<DurableToolsetMemberRegistration> members = [];

    internal string Id { get; } = id;

    internal bool IsImplicitDefault { get; } = isImplicitDefault;

    internal IReadOnlyList<DurableToolsetMemberRegistration> Members => members;

    internal IReadOnlyList<string> FunctionNames => members.Select(member => member.Declaration.Name).ToList();

    internal void Add(DurableRegisteredTool tool)
    {
        var functionName = tool.Declaration.Name;
        if (members.Any(member => string.Equals(
            member.Declaration.Name,
            functionName,
            StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Toolset '{Id}' already contains a function named '{functionName}'. " +
                "Function names use exact ordinal comparison.");
        }

        var memberIndex = members.Count;
        members.Add(new DurableToolsetMemberRegistration(
            $"tai-tool-v1:{Id.Length}:{Id}:{memberIndex}",
            tool.Declaration,
            tool.Options,
            tool.Function,
            tool.ActivationFactory));
    }
}

internal sealed record DurableRegisteredTool(
    Internal.DurableFunctionDeclarationSnapshot Declaration,
    DurableChatToolOptions Options,
    AIFunction? Function,
    Internal.IDurableToolActivationFactory? ActivationFactory);

internal sealed record DurableToolsetMemberRegistration(
    string ActivationKey,
    Internal.DurableFunctionDeclarationSnapshot Declaration,
    DurableChatToolOptions Options,
    AIFunction? Function,
    Internal.IDurableToolActivationFactory? ActivationFactory);

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
        : base(StringComparer.Ordinal)
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
/// <see langword="null"/> for <c>configure</c>. Worker-owned manifest resolution consumes this
/// policy through the toolset registration; the advanced caller-owned input factory uses the
/// registry directly.
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
        : base(StringComparer.Ordinal)
    {
        if (configurators is null) return;

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }
}
