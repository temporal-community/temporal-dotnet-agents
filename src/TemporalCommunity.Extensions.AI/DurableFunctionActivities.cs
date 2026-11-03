using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    public Task<DurableFunctionOutput> InvokeFunctionAsync(DurableFunctionInput input) =>
        RunBoundedActivityAsync(input);

    private async Task<DurableFunctionOutput> RunBoundedActivityAsync(
        DurableFunctionInput input)
    {
        try
        {
            return await InvokeFunctionCoreAsync(input).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogFunctionFailed(exception, input.FunctionName);
            throw Internal.DurableActivityFailurePolicy.CreateBounded(
                exception,
                "DurableToolInvocationFailed");
        }
    }

    private async Task<DurableFunctionOutput> InvokeFunctionCoreAsync(DurableFunctionInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        Internal.DurableToolFactoryActivation? activation = null;
        AIFunction function;
        if (input.ActivationKey is null
            && (input.ToolsetId is not null
                || input.MemberIdentityFingerprint is not null
                || input.ManifestFingerprint is not null
                || input.AuthorityBindingFingerprint is not null))
        {
            throw WorkerOwnedBindingFailure();
        }

        if (input.ActivationKey is not null)
        {
            ValidateWorkerOwnedBinding(input);
            if (toolsetActivationCatalog is null
                || !toolsetActivationCatalog.TryGetValue(input.ActivationKey, out var toolsetActivation)
                || !string.Equals(toolsetActivation.ToolsetId, input.ToolsetId, StringComparison.Ordinal)
                || !string.Equals(
                    toolsetActivation.Member.Declaration.Name,
                    input.FunctionName,
                    StringComparison.Ordinal))
            {
                throw WorkerOwnedBindingFailure();
            }

            var member = toolsetActivation.Member;
            var currentIdentity = Internal.DurableToolsetMemberIdentityFingerprint.Create(
                toolsetActivation.ToolsetId,
                member.ActivationKey,
                member.Declaration);
            if (!string.Equals(
                currentIdentity,
                input.MemberIdentityFingerprint,
                StringComparison.Ordinal))
            {
                throw WorkerOwnedBindingFailure();
            }

            if (member.ActivationFactory is not null)
            {
                activation = CreateFactoryActivation(member.ActivationFactory, input, ctx);
                function = activation.Function;
            }
            else
            {
                if (member.Function is null)
                {
                    throw WorkerOwnedBindingFailure();
                }

                function = member.Function;
            }

            ValidateImplementation(input.Declaration, function);
        }
        else if (factoryRegistry is not null
            && factoryRegistry.TryGetValue(input.FunctionName, out var activationFactory))
        {
            activation = CreateFactoryActivation(activationFactory, input, ctx);
            function = activation.Function;
            ValidateImplementation(input.Declaration, function);

            if (input.DispatchMode == DurableToolDispatchMode.Parallel
                && activation.CompleteState is not null)
            {
                RecordValidationRejection(Internal.DurableToolsetValidationReasons.InvalidPolicy);
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
            ValidateImplementation(input.Declaration, function);
        }

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ExecuteToolOperationName} {input.FunctionName}",
            ActivityKind.Internal);

        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ExecuteToolOperationName);
        span?.SetTag(DurableChatTelemetry.ToolNameAttribute, input.FunctionName);
        span?.SetTag(DurableChatTelemetry.ToolTypeAttribute, "function");
        SetBoundedTag(span, DurableChatTelemetry.ToolCallIdAttribute, input.ToolCallId, 128);
        SetBoundedTag(span, DurableChatTelemetry.GenAiConversationIdAttribute, input.ConversationId, 128);
        SetBoundedTag(span, DurableChatTelemetry.ToolDescriptionAttribute, input.Declaration?.Description, 256);
        var captureTelemetryContent = Internal.DurableTelemetryContentCapturePolicy.Resolve(
            serviceProvider?.GetService<DurableExecutionOptions>());
        if (captureTelemetryContent && span is not null)
        {
            SetBoundedTag(
                span,
                DurableChatTelemetry.ToolCallArgumentsAttribute,
                SerializeBoundedTelemetryContent(input.Arguments),
                4096);
        }

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
            var output = new DurableFunctionOutput
            {
                Result = result,
                EffectCompleted = true,
                HasStateReplacement = stateUpdate.HasReplacement,
                StateReplacement = stateUpdate.Value,
            };
            if (input.EnableTransportPayloadGuard)
            {
                try
                {
                    Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                        ctx.PayloadConverter,
                        output,
                        input.MaximumTransportPayloadSizeBytes,
                        input.PayloadCodecExpansionAllowanceBytes,
                        "tool activity result");
                }
                catch (Exceptions.DurableTransportPayloadTooLargeException)
                {
                    output = new DurableFunctionOutput
                    {
                        Status = DurableFunctionOutputStatus.ResultTooLarge,
                        Result = DurableFunctionOutput.ResultTooLargeMessage,
                        EffectCompleted = true,
                        HasStateReplacement = stateUpdate.HasReplacement,
                        StateReplacement = stateUpdate.Value,
                    };
                    try
                    {
                        Internal.DurableTransportPayloadGuard.EnsureWithinLimit(
                            ctx.PayloadConverter,
                            output,
                            input.MaximumTransportPayloadSizeBytes,
                            input.PayloadCodecExpansionAllowanceBytes,
                            "bounded tool activity result");
                    }
                    catch (Exception exception)
                    {
                        throw Internal.DurableActivityFailurePolicy.CreateBounded(
                            exception,
                            "DurableToolResultTooLarge",
                            nonRetryable: true);
                    }
                }
                catch (Exception exception)
                {
                    throw Internal.DurableActivityFailurePolicy.CreateBounded(
                        exception,
                        "DurableToolResultSerializationFailed",
                        nonRetryable: true);
                }
            }

            if (captureTelemetryContent
                && span is not null
                && output.Status == DurableFunctionOutputStatus.Succeeded)
            {
                SetBoundedTag(
                    span,
                    DurableChatTelemetry.ToolCallResultAttribute,
                    SerializeBoundedTelemetryContent(output.Result),
                    4096);
            }

            return output;
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error);
            span?.SetTag(DurableChatTelemetry.ErrorTypeAttribute, GetBoundedErrorType(ex));
            throw;
        }
    }

    private static void SetBoundedTag(Activity? activity, string name, string? value, int maximumLength)
    {
        if (!string.IsNullOrEmpty(value))
        {
            activity?.SetTag(name, value.Length <= maximumLength
                ? value
                : value[..maximumLength]);
        }
    }

    private string? SerializeBoundedTelemetryContent(object? value)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(value, DurableAIJsonUtilities.DefaultOptions);
            const string truncatedMarker = "...[truncated]";
            return serialized.Length <= 4096
                ? serialized
                : string.Concat(serialized.AsSpan(0, 4096 - truncatedMarker.Length), truncatedMarker);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Telemetry is best-effort; serialization must not invalidate a completed tool effect.
            _logger.LogWarning(
                "Could not serialize tool telemetry content ({ExceptionType}); omitting the attribute.",
                exception.GetType().Name);
            return null;
        }
    }

    private static string GetBoundedErrorType(Exception exception)
    {
        var errorType = exception is ApplicationFailureException { ErrorType: { Length: > 0 } type }
            ? type
            : exception.GetType().Name;
        return errorType.Length <= 64 ? errorType : errorType[..64];
    }

    private void ValidateWorkerOwnedBinding(DurableFunctionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ToolsetId)
            || string.IsNullOrWhiteSpace(input.MemberIdentityFingerprint)
            || input.Declaration is null
            || !IsVersionOneFingerprint(input.ManifestFingerprint, "tai-toolset-v1:")
            || !IsVersionOneFingerprint(
                input.MemberIdentityFingerprint,
                "tai-tool-member-v1:")
            || !IsVersionOneFingerprint(
                input.AuthorityBindingFingerprint,
                "tai-tool-binding-v1:"))
        {
            throw WorkerOwnedBindingFailure();
        }

        try
        {
            input.Declaration.Validate();
        }
        catch
        {
            RecordValidationRejection(Internal.DurableToolsetValidationReasons.InvalidDeclaration);
            throw;
        }
        var carriedIdentity = Internal.DurableToolsetMemberIdentityFingerprint.Create(
            input.ToolsetId,
            input.ActivationKey!,
            input.Declaration);
        if (!string.Equals(
            carriedIdentity,
            input.MemberIdentityFingerprint,
            StringComparison.Ordinal)
            || !string.Equals(
                Internal.DurableToolsetAuthorityBindingFingerprint.Create(
                    input.ManifestFingerprint!,
                    input.MemberIdentityFingerprint),
                input.AuthorityBindingFingerprint,
                StringComparison.Ordinal))
        {
            throw WorkerOwnedBindingFailure();
        }
    }

    private static bool IsVersionOneFingerprint(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 64
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = prefix.Length; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private ApplicationFailureException WorkerOwnedBindingFailure()
    {
        RecordValidationRejection(Internal.DurableToolsetValidationReasons.ManifestMismatch);
        return new ApplicationFailureException(
            "The durable tool activity input does not match its recorded manifest member.",
            new Internal.DurableToolsetValidationException(
                Internal.DurableToolsetValidationReasons.ManifestMismatch,
                "The durable tool activity input does not match its recorded manifest member."),
            errorType: nameof(Exceptions.DurableConfigurationException),
            nonRetryable: true);
    }

    private void ValidateImplementation(
        Internal.DurableFunctionDeclarationSnapshot? declaration,
        AIFunction function)
    {
        try
        {
            declaration?.ValidateImplementation(function);
        }
        catch
        {
            RecordValidationRejection(Internal.DurableToolsetValidationReasons.InvalidDeclaration);
            throw;
        }
    }

    private void RecordValidationRejection(string reason)
    {
        var tags = new TagList { { "reason", reason } };
        DurableChatTelemetry.ToolsetValidationRejections.Add(1, tags);
        _logger.LogToolsetValidationRejected(reason);
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
        var idempotencyKey = input.IdempotencyKeyVersion switch
        {
            Internal.DurableToolIdempotencyKey.ActivityIdentityVersion =>
                Internal.DurableToolIdempotencyKey.Create(
                    input.IdempotencyKeyVersion,
                    info.Namespace,
                    workflowId,
                    workflowRunId,
                    info.ActivityId),
            Internal.DurableToolIdempotencyKey.CurrentVersion =>
                Internal.DurableToolIdempotencyKey.CreateLogicalCall(
                    input.IdempotencyKeyVersion,
                    info.Namespace,
                    workflowId,
                    input.SessionEpoch!,
                    input.RequestId!,
                    input.TurnNumber
                        ?? throw new ApplicationFailureException(
                            "Durable tool turn number is missing.",
                            errorType: nameof(Exceptions.DurableConfigurationException),
                            nonRetryable: true),
                    input.FunctionName,
                    input.ToolCallId,
                    input.ModelIteration,
                    input.CallIndex),
            _ => throw new ApplicationFailureException(
                $"Unsupported durable tool idempotency-key version '{input.IdempotencyKeyVersion}'.",
                errorType: nameof(Exceptions.DurableConfigurationException),
                nonRetryable: true),
        };
        var metadata = new DurableToolInvocationMetadata(
            info.Namespace,
            workflowId,
            workflowRunId,
            info.ActivityId,
            input.SessionEpoch,
            input.RequestId,
            input.TurnNumber,
            info.Attempt,
            info.TaskQueue,
            input.FunctionName,
            input.ToolCallId,
            input.ModelIteration,
            input.CallIndex,
            input.ConversationId,
            input.CorrelationId,
            idempotencyKey,
            input.IsIdempotent);
        return activationFactory.Create(
            serviceProvider ?? throw new InvalidOperationException(
                "Invocation-scoped durable tools require an activity service provider."),
            input,
            metadata);
    }
}
