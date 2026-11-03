using System.Text;
using System.Text.Json;
using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Approvals;

internal static class DurableApprovalReviewDataGuard
{
    internal const int MaxEntries = 64;
    internal const int MaxSerializedBytes = 16 * 1024;

    internal static void EnsureWithinLimit(
        string? description,
        IReadOnlyDictionary<string, string>? reviewData)
    {
        if (reviewData is { Count: > MaxEntries })
        {
            throw TooLarge();
        }

        long contentBytes = Encoding.UTF8.GetByteCount(description ?? string.Empty);
        if (contentBytes > MaxSerializedBytes)
        {
            throw TooLarge();
        }

        if (reviewData is not null)
        {
            foreach (var (key, value) in reviewData)
            {
                if (key is null || value is null)
                {
                    throw new ApplicationFailureException(
                        "Approval review metadata keys and values must not be null.",
                        errorType: "DurableApprovalReviewProjectionInvalid",
                        nonRetryable: true);
                }

                contentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
                if (contentBytes > MaxSerializedBytes)
                {
                    throw TooLarge();
                }
            }
        }

        var serialized = JsonSerializer.SerializeToUtf8Bytes(new { description, reviewData });
        if (serialized.Length > MaxSerializedBytes)
        {
            throw TooLarge();
        }
    }

    private static ApplicationFailureException TooLarge() =>
        new(
            $"The approval review projection exceeds the {MaxSerializedBytes}-byte limit.",
            errorType: "DurableApprovalReviewProjectionTooLarge",
            nonRetryable: true);
}
