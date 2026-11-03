using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableTransportPayloadGuard
{
    internal static void EnsureWithinLimit(
        IPayloadConverter payloadConverter,
        object? value,
        int maximumPayloadSizeBytes,
        int payloadCodecExpansionAllowanceBytes,
        string boundary)
    {
        ArgumentNullException.ThrowIfNull(payloadConverter);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        if (maximumPayloadSizeBytes <= 0)
        {
            throw new InvalidOperationException("The maximum transport payload size must be positive.");
        }

        if (payloadCodecExpansionAllowanceBytes < 0
            || payloadCodecExpansionAllowanceBytes >= maximumPayloadSizeBytes)
        {
            throw new InvalidOperationException(
                "The payload-codec expansion allowance must be non-negative and smaller than " +
                "the maximum transport payload size.");
        }

        var serializedSizeBytes = payloadConverter.ToPayload(value).CalculateSize();
        if (serializedSizeBytes > maximumPayloadSizeBytes - payloadCodecExpansionAllowanceBytes)
        {
            var exception = new Exceptions.DurableTransportPayloadTooLargeException(
                boundary,
                serializedSizeBytes,
                maximumPayloadSizeBytes,
                payloadCodecExpansionAllowanceBytes);
            if (Workflow.InWorkflow)
            {
                throw new ApplicationFailureException(
                    exception.Message,
                    exception,
                    errorType: "DurableTransportPayloadTooLarge",
                    nonRetryable: true);
            }

            throw exception;
        }
    }

    internal static void EnsureBoundedRetryPolicy(
        Temporalio.Common.RetryPolicy? retryPolicy,
        string settingName)
    {
        if (retryPolicy is { MaximumAttempts: <= 0 })
        {
            throw new InvalidOperationException(
                $"{settingName} must specify a positive MaximumAttempts value. " +
                "Unbounded activity retries are not supported.");
        }
    }
}
