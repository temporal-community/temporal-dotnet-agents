using Microsoft.Extensions.AI;
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
        DurableAIServiceCollectionExtensions.RegisterDurableFunction(
            worker.Services,
            function,
            configure);
        registration.Add(function.Name);
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
        DurableAIServiceCollectionExtensions.RegisterDurableToolFactory(
            worker,
            declaration,
            factory,
            configure);
        registration.Add(declaration.Name);
        return this;
    }
}
