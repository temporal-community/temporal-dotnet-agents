using System.Text;
using Temporalio.Exceptions;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableActivityFailurePolicy
{
    private const int MaximumErrorTypeBytes = 128;
    private const int MaximumSafeConfigurationMessageBytes = 512;
    private const string BoundedFailureMessage = "Durable activity failed; failure details were omitted.";

    internal static ApplicationFailureException CreateBounded(
        Exception exception,
        string fallbackErrorType,
        bool? nonRetryable = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackErrorType);

        var applicationFailure = exception as ApplicationFailureException;
        var errorType = applicationFailure?.ErrorType;
        if (string.IsNullOrWhiteSpace(errorType))
        {
            errorType = fallbackErrorType;
        }

        var isKnownSafeConfigurationFailure =
            applicationFailure?.InnerException is DurableMixedPatternException
            || applicationFailure?.Message.StartsWith(
                "ChatOptions.Tools is not supported by durable execution.",
                StringComparison.Ordinal) == true;
        var message = isKnownSafeConfigurationFailure
            ? TruncateUtf8(applicationFailure!.Message, MaximumSafeConfigurationMessageBytes)
            : BoundedFailureMessage;
        var boundedInner = applicationFailure?.InnerException is DurableMixedPatternException mixed
            ? new DurableMixedPatternException(
                TruncateUtf8(mixed.Message, MaximumSafeConfigurationMessageBytes))
            : null;

        return new ApplicationFailureException(
            message,
            boundedInner,
            errorType: TruncateUtf8(errorType, MaximumErrorTypeBytes),
            nonRetryable: nonRetryable ?? applicationFailure?.NonRetryable ?? false);
    }

    internal static string TruncateUtf8(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var usedBytes = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            var isPair = char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]);
            var characterBytes = isPair
                ? 4
                : char.IsSurrogate(current)
                    ? 3
                    : current <= 0x7f
                        ? 1
                        : current <= 0x7ff
                            ? 2
                            : 3;
            if (usedBytes + characterBytes > maximumBytes)
            {
                break;
            }

            if (isPair)
            {
                builder.Append(current);
                builder.Append(value[++index]);
            }
            else
            {
                builder.Append(char.IsSurrogate(current) ? '\uFFFD' : current);
            }
            usedBytes += characterBytes;
        }

        return builder.ToString();
    }
}
