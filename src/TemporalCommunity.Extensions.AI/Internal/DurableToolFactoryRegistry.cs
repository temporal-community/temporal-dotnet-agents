using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Internal;

internal interface IDurableToolActivationFactory
{
    DurableToolFactoryActivation Create(
        IServiceProvider services,
        DurableFunctionInput input,
        DurableToolInvocationMetadata metadata);
}

internal sealed class DurableToolFactoryActivation
{
    public required AIFunction Function { get; init; }

    public Func<object?, CancellationToken, ValueTask<DurableStateUpdateJson>>? CompleteState
    {
        get;
        init;
    }
}

internal readonly record struct DurableStateUpdateJson(bool HasReplacement, JsonElement? Value);

internal sealed class DurableToolActivationFactory<TRequestData, TTurnState>(
    Func<IServiceProvider, DurableToolInvocationContext<TRequestData, TTurnState>, DurableToolActivation<TTurnState>> factory)
    : IDurableToolActivationFactory
{
    public DurableToolFactoryActivation Create(
        IServiceProvider services,
        DurableFunctionInput input,
        DurableToolInvocationMetadata metadata)
    {
        if (input.RequestData is not { } requestDataJson)
        {
            throw new InvalidOperationException(
                $"Tool '{input.FunctionName}' requires typed request data, but none was scheduled.");
        }

        var requestData = requestDataJson.Deserialize<TRequestData>(DurableAIJsonUtilities.DefaultOptions)!;
        var turnState = input.TurnState is { } stateJson
            ? stateJson.Deserialize<TTurnState>(DurableAIJsonUtilities.DefaultOptions)
            : default;
        var context = new DurableToolInvocationContext<TRequestData, TTurnState>(
            requestData,
            turnState,
            input.DispatchMode,
            metadata);
        var activation = factory(services, context)
            ?? throw new InvalidOperationException(
                $"The invocation factory for '{input.FunctionName}' returned null.");

        return new DurableToolFactoryActivation
        {
            Function = activation.Function,
            CompleteState = activation.CompleteState is null
                ? null
                : async (result, cancellationToken) =>
                {
                    var update = await activation.CompleteState(result, cancellationToken)
                        .ConfigureAwait(false);
                    return new DurableStateUpdateJson(
                        update.HasReplacement,
                        update.HasReplacement
                            ? JsonSerializer.SerializeToElement(
                                update.Value,
                                DurableAIJsonUtilities.DefaultOptions)
                            : null);
                },
        };
    }
}

internal sealed class DurableToolFactoryRegistry
    : Dictionary<string, IDurableToolActivationFactory>
{
    internal DurableToolFactoryRegistry(
        IEnumerable<Action<DurableToolFactoryRegistry>>? configurators = null)
        : base(StringComparer.Ordinal)
    {
        if (configurators is null)
        {
            return;
        }

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }
}

internal sealed class DurableFunctionDeclarationRegistry
    : Dictionary<string, DurableFunctionDeclarationSnapshot>
{
    internal DurableFunctionDeclarationRegistry(
        IEnumerable<Action<DurableFunctionDeclarationRegistry>>? configurators = null)
        : base(StringComparer.Ordinal)
    {
        if (configurators is null)
        {
            return;
        }

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }
}
