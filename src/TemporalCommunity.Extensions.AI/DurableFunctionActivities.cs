using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Temporal activities that execute <see cref="AIFunction"/> invocations durably.
/// Functions are resolved from a DI-registered registry by name.
/// </summary>
internal sealed class DurableFunctionActivities(
    IReadOnlyDictionary<string, AIFunction> functionRegistry,
    ILoggerFactory? loggerFactory = null,
    Internal.DurableToolFactoryRegistry? factoryRegistry = null,
    IServiceProvider? serviceProvider = null,
    Internal.DurableToolsetActivationCatalog? toolsetActivationCatalog = null)
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
        if (input.ActivationKey is not null)
        {
            if (toolsetActivationCatalog is null
                || !toolsetActivationCatalog.TryGetValue(input.ActivationKey, out var toolsetActivation)
                || !string.Equals(toolsetActivation.ToolsetId, input.ToolsetId, StringComparison.Ordinal)
                || !string.Equals(
                    toolsetActivation.Member.Declaration.Name,
                    input.FunctionName,
                    StringComparison.Ordinal))
            {
                throw new ApplicationFailureException(
                    "The durable toolset activation does not match the recorded manifest member.",
                    errorType: nameof(Exceptions.DurableConfigurationException),
                    nonRetryable: true);
            }

            var member = toolsetActivation.Member;
            if (member.ActivationFactory is not null)
            {
                activation = CreateFactoryActivation(member.ActivationFactory, input, ctx);
                function = activation.Function;
            }
            else
            {
                function = member.Function
                    ?? throw new ApplicationFailureException(
                        "The durable toolset member has no worker implementation.",
                        errorType: nameof(Exceptions.DurableConfigurationException),
                        nonRetryable: true);
            }

            input.Declaration?.ValidateImplementation(function);
        }
        else if (factoryRegistry is not null
            && factoryRegistry.TryGetValue(input.FunctionName, out var activationFactory))
        {
            activation = CreateFactoryActivation(activationFactory, input, ctx);
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
            arguments.Services = serviceProvider;

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

    private Internal.DurableToolFactoryActivation CreateFactoryActivation(
        Internal.IDurableToolActivationFactory activationFactory,
        DurableFunctionInput input,
        ActivityExecutionContext context)
    {
        if (!ActivityExecutionContext.HasCurrent)
        {
            throw new InvalidOperationException(
                "Invocation-scoped durable tools require an activity execution context.");
        }

        var info = context.Info;
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
        return activationFactory.Create(
            serviceProvider ?? throw new InvalidOperationException(
                "Invocation-scoped durable tools require an activity service provider."),
            input,
            metadata);
    }
}
