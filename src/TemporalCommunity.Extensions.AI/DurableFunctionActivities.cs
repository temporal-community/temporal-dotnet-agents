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
    ILoggerFactory? loggerFactory = null)
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

        if (!functionRegistry.TryGetValue(input.FunctionName, out var function))
        {
            throw new InvalidOperationException(
                $"Function '{input.FunctionName}' is not registered in the durable function registry.");
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

            _logger.LogFunctionCompleted(input.FunctionName);
            return new DurableFunctionOutput { Result = result };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogFunctionFailed(ex, input.FunctionName);
            throw;
        }
    }
}
