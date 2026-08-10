using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal readonly struct DurableStateUpdate<TState>
{
    private DurableStateUpdate(bool hasReplacement, TState? value)
    {
        HasReplacement = hasReplacement;
        Value = value;
    }

    public bool HasReplacement { get; }

    public TState? Value { get; }

    public static DurableStateUpdate<TState> Unchanged => default;

    public static DurableStateUpdate<TState> Replace(TState? value) => new(true, value);
}

internal sealed class DurableToolInvocationMetadata
{
    public required string Namespace { get; init; }
    public required string WorkflowId { get; init; }
    public required string WorkflowRunId { get; init; }
    public required string ActivityId { get; init; }
    public required int Attempt { get; init; }
    public required string TaskQueue { get; init; }
    public required string ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public required int ModelIteration { get; init; }
    public required int CallIndex { get; init; }
    public string? ConversationId { get; init; }
    public string? CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
}

internal sealed class DurableToolInvocationContext<TRequestData, TState>
{
    public required TRequestData RequestData { get; init; }
    public TState? TurnState { get; init; }
    public required DurableToolDispatchMode DispatchMode { get; init; }
    public required DurableToolInvocationMetadata Metadata { get; init; }
}

internal sealed class DurableToolActivation<TState>
{
    public required AIFunction Function { get; init; }

    public Func<object?, CancellationToken, ValueTask<DurableStateUpdate<TState>>>? CompleteState
    {
        get; init;
    }
}

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
