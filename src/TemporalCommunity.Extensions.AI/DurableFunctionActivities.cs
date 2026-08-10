using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Temporal activities that execute <see cref="AIFunction"/> invocations durably.
/// Functions are resolved from a DI-registered registry by name.
/// </summary>
internal sealed class DurableFunctionActivities(
    IReadOnlyDictionary<string, AIFunction> functionRegistry,
    ILoggerFactory? loggerFactory = null,
    Internal.DurableToolFactoryRegistry? factoryRegistry = null,
    IServiceProvider? serviceProvider = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableFunctionActivities>();

    /// <summary>
    /// Invokes a named <see cref="AIFunction"/> with the given arguments.
    /// </summary>
    [Activity("TemporalCommunity.Extensions.AI.InvokeFunction")]
    public async Task<DurableFunctionOutput> InvokeFunctionAsync(DurableFunctionInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        Internal.DurableToolFactoryActivation? activation = null;
        AIFunction function;
        if (factoryRegistry is not null
            && factoryRegistry.TryGetValue(input.FunctionName, out var activationFactory))
        {
            if (!ActivityExecutionContext.HasCurrent)
            {
                throw new InvalidOperationException(
                    "Invocation-scoped durable tools require an activity execution context.");
            }

            var info = ctx.Info;
            var workflowId = info.WorkflowId
                ?? throw new InvalidOperationException("Activity workflow ID is missing.");
            var workflowRunId = info.WorkflowRunId
                ?? throw new InvalidOperationException("Activity workflow run ID is missing.");
            var idempotencyKey = Internal.DurableToolIdempotencyKey.Create(
                input.IdempotencyKeyVersion,
                info.Namespace,
                workflowId,
                workflowRunId,
                info.ActivityId);
            var metadata = new DurableToolInvocationMetadata(
                info.Namespace,
                workflowId,
                workflowRunId,
                info.ActivityId,
                info.Attempt,
                info.TaskQueue,
                input.FunctionName,
                input.ToolCallId,
                input.ModelIteration,
                input.CallIndex,
                input.ConversationId,
                input.CorrelationId,
                idempotencyKey);
            activation = activationFactory.Create(
                serviceProvider ?? throw new InvalidOperationException(
                    "Invocation-scoped durable tools require an activity service provider."),
                input,
                metadata);
            function = activation.Function;
            input.Declaration?.ValidateImplementation(function);

            if (input.DispatchMode == DurableToolDispatchMode.Parallel
                && activation.CompleteState is not null)
            {
                throw new Temporalio.Exceptions.ApplicationFailureException(
                    "A durable tool cannot complete turn state while parallel dispatch is enabled.",
                    errorType: nameof(Exceptions.DurableConfigurationException),
                    nonRetryable: true);
            }
        }
        else if (!functionRegistry.TryGetValue(input.FunctionName, out function!))
        {
            throw new InvalidOperationException(
                $"Function '{input.FunctionName}' is not registered in the durable function registry.");
        }
        else
        {
            input.Declaration?.ValidateImplementation(function);
        }

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ExecuteToolOperationName} {input.FunctionName}",
            ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ExecuteToolOperationName);
        span?.SetTag(DurableChatTelemetry.ToolNameAttribute, input.FunctionName);

        _logger.LogFunctionInvoking(input.FunctionName);

        try
        {
            // Build AIFunctionArguments from the deserialized dictionary.
            var arguments = input.Arguments is not null
                ? new AIFunctionArguments(input.Arguments)
                : new AIFunctionArguments();

            var result = await function.InvokeAsync(arguments, ct).ConfigureAwait(false);

            Internal.DurableStateUpdateJson stateUpdate = default;
            if (activation?.CompleteState is not null)
            {
                try
                {
                    stateUpdate = await activation.CompleteState(result, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new Temporalio.Exceptions.ApplicationFailureException(
                        "Durable tool state completion failed after function invocation.",
                        exception,
                        errorType: nameof(Exceptions.DurableConfigurationException),
                        nonRetryable: true);
                }
            }

            _logger.LogFunctionCompleted(input.FunctionName);
            return new DurableFunctionOutput
            {
                Result = result,
                HasStateReplacement = stateUpdate.HasReplacement,
                StateReplacement = stateUpdate.Value,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogFunctionFailed(ex, input.FunctionName);
            throw;
        }
    }
}
