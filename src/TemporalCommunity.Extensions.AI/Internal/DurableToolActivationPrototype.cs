using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed class DurableToolActivationResult
{
    public object? ModelResult { get; init; }

    public bool HasStateReplacement { get; init; }

    public JsonElement? StateReplacement { get; init; }
}

internal static class DurableToolActivationInvoker
{
    public static async Task<DurableToolActivationResult> InvokeAsync<TState>(
        DurableToolActivation<TState> activation,
        AIFunctionArguments arguments,
        DurableToolDispatchMode dispatchMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(arguments);

        if (dispatchMode == DurableToolDispatchMode.Parallel && activation.CompleteState is not null)
        {
            throw NonRetryableConfigurationFailure(
                "A durable tool cannot complete turn state while parallel dispatch is enabled.");
        }

        var modelResult = await activation.Function
            .InvokeAsync(arguments, cancellationToken)
            .ConfigureAwait(false);

        if (activation.CompleteState is null)
        {
            return new DurableToolActivationResult { ModelResult = modelResult };
        }

        try
        {
            var update = await activation.CompleteState(modelResult, cancellationToken)
                .ConfigureAwait(false);
            return new DurableToolActivationResult
            {
                ModelResult = modelResult,
                HasStateReplacement = update.HasReplacement,
                StateReplacement = update.HasReplacement
                    ? JsonSerializer.SerializeToElement(update.Value, DurableAIJsonUtilities.DefaultOptions)
                    : null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ApplicationFailureException(
                "Durable tool state completion failed after function invocation.",
                exception,
                errorType: nameof(DurableConfigurationException),
                nonRetryable: true);
        }
    }

    private static ApplicationFailureException NonRetryableConfigurationFailure(string message) =>
        new(
            message,
            errorType: nameof(DurableConfigurationException),
            nonRetryable: true);
}
