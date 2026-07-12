using Temporalio.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Creates the non-retryable Temporal failure used for deterministic LLM-provider errors.
/// </summary>
internal static class LlmFailurePolicy
{
    /// <summary>Error type stamped on deterministic LLM-provider failures.</summary>
    internal const string NonRetryableErrorType = "LlmNonRetryable";

    /// <summary>
    /// Returns a non-retryable activity failure when <paramref name="exception"/> has a known
    /// deterministic provider status; otherwise returns <see langword="null"/> so the caller
    /// can preserve the original exception and stack trace.
    /// </summary>
    internal static ApplicationFailureException? CreateNonRetryableFailure(Exception exception) =>
        LlmErrorClassifier.IsNonRetryable(exception)
            ? new ApplicationFailureException(
                $"Non-retryable LLM error: {exception.Message}",
                exception,
                errorType: NonRetryableErrorType,
                nonRetryable: true)
            : null;
}
