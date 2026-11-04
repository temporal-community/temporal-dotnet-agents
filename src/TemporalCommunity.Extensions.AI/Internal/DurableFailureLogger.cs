using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableFailureLogger
{
    private const int MaximumLogTypeLength = 64;
    private const int MaximumContextLength = 256;

    internal static void LogException(
        ILogger logger,
        Exception exception,
        bool captureTelemetryContent,
        string operation,
        string context,
        string? fallbackErrorType = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(context);
        operation = Bound(operation);
        context = Bound(context);

        if (captureTelemetryContent)
        {
            Logs.LogActivityFailureWithException(logger, exception, operation, context);
            return;
        }

        var rootException = exception.GetBaseException();
        var exceptionType = Bound(rootException.GetType().Name);
        var errorType = exception is ApplicationFailureException
            { ErrorType: { Length: > 0 } applicationErrorType }
            ? applicationErrorType
            : fallbackErrorType ?? rootException.GetType().Name;
        var boundedErrorType = Bound(errorType);
        int? statusCode = LlmErrorClassifier.TryGetHttpStatusCode(exception, out var status)
            ? status
            : null;

        Logs.LogActivityFailureSanitized(
            logger,
            operation,
            context,
            exceptionType,
            boundedErrorType,
            statusCode);
        Logs.LogActivityFailureDetails(logger, exception, operation, context);
    }

    private static string Bound(string value) =>
        DurableActivityFailurePolicy.TruncateUtf8(value, MaximumLogTypeLength);
}
