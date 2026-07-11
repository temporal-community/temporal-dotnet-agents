using System.Net;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Classifies exceptions thrown by an <see cref="global::Microsoft.Extensions.AI.IChatClient"/>
/// invocation as retryable (transient — worth a durable Temporal retry) or non-retryable
/// (deterministic — retrying would loop forever and hang the workflow).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> LLM-call activities dispatch with <c>RetryPolicy = null</c> historically,
/// which the Temporal server treats as its default policy — <c>MaximumAttempts = 0</c>, i.e.
/// <em>unlimited</em> retries. A deterministic, non-transient provider error (HTTP 400/401/403/404/422,
/// or a scripted/exhausted client) then retries forever, the workflow never completes, and the
/// caller's <c>SendAsync</c> update never returns. This classifier lets the activity fail fast on
/// errors that will never succeed on retry, so the workflow can surface a terminal failure instead
/// of hanging.
/// </para>
/// <para>
/// <b>Classification is by HTTP status bucket, never by message body.</b> Content-filter and
/// context-length errors surface as HTTP 400 and are treated as non-retryable by the 400 rule — we
/// intentionally do NOT string-match error bodies (brittle across providers and locales).
/// </para>
/// <para>
/// <b>Default is retryable.</b> Anything not positively identified as a non-retryable status is
/// treated as retryable so genuine transients and provider outages still get durable retry. This is
/// the security-reviewed posture: do not fail-fast on unknown errors. The bounded default
/// <c>RetryPolicy { MaximumAttempts = 5 }</c> (applied at session start) is the backstop that keeps
/// even "retryable-but-permanently-broken" errors from looping forever.
/// </para>
/// </remarks>
internal static class LlmErrorClassifier
{
    // Deterministic client-side / auth / not-found / unprocessable statuses. Retrying these
    // produces the same result every time, so they fail fast.
    private static readonly HashSet<int> NonRetryableStatuses = new()
    {
        400, // Bad Request (includes content-filter, context-length-exceeded)
        401, // Unauthorized (bad/expired API key)
        403, // Forbidden (key lacks permission / region blocked)
        404, // Not Found (unknown model / deployment)
        422, // Unprocessable Entity (invalid request shape)
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents a deterministic,
    /// non-transient LLM error that should NOT be retried (fail fast). Returns
    /// <see langword="false"/> for retryable/transient errors and for anything not positively
    /// classified as non-retryable (default-retryable posture).
    /// </summary>
    /// <param name="ex">The exception thrown by the chat-client invocation.</param>
    public static bool IsNonRetryable(Exception? ex)
    {
        foreach (var candidate in Unwrap(ex))
        {
            if (TryGetHttpStatus(candidate, out var status) && NonRetryableStatuses.Contains(status))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Yields the exception and any relevant inner/aggregated exceptions so a status buried in an
    /// <see cref="AggregateException"/> or wrapped by a delegating chat client is still inspected.
    /// </summary>
    private static IEnumerable<Exception> Unwrap(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AggregateException agg)
            {
                foreach (var flattened in agg.Flatten().InnerExceptions)
                {
                    foreach (var nested in Unwrap(flattened))
                    {
                        yield return nested;
                    }
                }

                yield break;
            }

            yield return ex;
            ex = ex.InnerException;
        }
    }

    /// <summary>
    /// Attempts to read an HTTP status code from a single exception. Two provider paths are
    /// supported:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="System.Net.Http.HttpRequestException.StatusCode"/> — provider-agnostic BCL
    ///     path (always available; used by many HTTP-based clients).
    ///   </item>
    ///   <item>
    ///     <c>System.ClientModel.ClientResultException.Status</c> — the OpenAI / Azure OpenAI path.
    ///     Detected by type-name + reflected <c>Status</c> property so this library needs no hard
    ///     package reference on <c>System.ClientModel</c> and stays provider-agnostic.
    ///   </item>
    /// </list>
    /// </summary>
    private static bool TryGetHttpStatus(Exception ex, out int status)
    {
#if NET5_0_OR_GREATER
        // HttpRequestException.StatusCode is a net5.0+ API — absent on netstandard2.1.
        // On the down-level leg a raw HttpRequestException carries no status code, so it
        // cannot be deterministically classified and falls through to default-retryable
        // (bounded by the retry backstop). This is an accepted down-level limitation for
        // arbitrary non-OpenAI HTTP providers; the OpenAI/Azure path below still classifies
        // via ClientResultException.Status on both TFMs.
        if (ex is HttpRequestException { StatusCode: { } httpStatus })
        {
            status = (int)httpStatus;
            return true;
        }
#endif

        // System.ClientModel.ClientResultException — the OpenAI/Azure OpenAI SDK error type.
        // Read its int Status property via reflection to avoid taking a package dependency.
        if (ex.GetType().FullName == "System.ClientModel.ClientResultException")
        {
            var statusProperty = ex.GetType().GetProperty("Status");
            if (statusProperty is not null
                && statusProperty.PropertyType == typeof(int)
                && statusProperty.GetValue(ex) is int clientModelStatus)
            {
                status = clientModelStatus;
                return true;
            }
        }

        status = 0;
        return false;
    }
}
