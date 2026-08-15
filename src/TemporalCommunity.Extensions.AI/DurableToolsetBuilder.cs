using Microsoft.Extensions.AI;
using System.Reflection;
using Temporalio.Extensions.Hosting;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Builds one ordered, worker-owned set of durable tools.
/// </summary>
/// <remarks>
/// Instances are supplied only to <see cref="DurableAIServiceCollectionExtensions.AddDurableToolset"/>.
/// Toolset identifiers and model-visible function names use exact ordinal comparison.
/// </remarks>
public sealed class DurableToolsetBuilder
{
    private readonly ITemporalWorkerServiceOptionsBuilder worker;
    private readonly DurableToolsetRegistration registration;

    internal DurableToolsetBuilder(
        ITemporalWorkerServiceOptionsBuilder worker,
        DurableToolsetRegistration registration)
    {
        this.worker = worker;
        this.registration = registration;
    }

    /// <summary>Adds an already-created function to this toolset.</summary>
    public DurableToolsetBuilder Add(
        AIFunction function,
        Action<DurableChatToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        registration.Add(DurableAIServiceCollectionExtensions.RegisterDurableFunction(
            worker.Services,
            function,
            configure));
        return this;
    }

    /// <summary>
    /// Adds a stable declaration and an activity-attempt factory to this toolset.
    /// </summary>
    public DurableToolsetBuilder AddDurableToolFactory<TRequestData, TTurnState>(
        AIFunctionDeclaration declaration,
        Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory,
        Action<DurableChatToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(factory);
        registration.Add(DurableAIServiceCollectionExtensions.RegisterDurableToolFactory(
            worker,
            declaration,
            factory,
            configure));
        return this;
    }

    /// <summary>
    /// Adds one explicitly selected handler method. A fresh handler is created from the activity
    /// scope for every invocation while its declaration and activator are cached at registration.
    /// </summary>
    public DurableToolsetBuilder AddDurableToolFactory<THandler>(
        string methodName,
        AIFunctionFactoryOptions? functionOptions = null,
        Action<DurableChatToolOptions>? configure = null)
        where THandler : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        var method = DurableAIServiceCollectionExtensions.ResolveMethod<THandler>(methodName);
        return AddMethod<THandler>(method, functionOptions, configure);
    }

    /// <summary>Adds one explicitly selected handler method, including an overload.</summary>
    public DurableToolsetBuilder AddDurableToolFactory<THandler>(
        MethodInfo method,
        AIFunctionFactoryOptions? functionOptions = null,
        Action<DurableChatToolOptions>? configure = null)
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(method);
        return AddMethod<THandler>(method, functionOptions, configure);
    }

    private DurableToolsetBuilder AddMethod<THandler>(
        MethodInfo method,
        AIFunctionFactoryOptions? functionOptions,
        Action<DurableChatToolOptions>? configure)
        where THandler : class
    {
        DurableAIServiceCollectionExtensions.ValidateMethod<THandler>(method);
        var registered = DurableAIServiceCollectionExtensions.RegisterMethodTool<THandler>(
            worker.Services,
            method,
            functionOptions,
            configure);
        registration.Add(registered);
        return this;
    }
}
