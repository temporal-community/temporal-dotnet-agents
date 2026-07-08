using Temporalio.Common;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Provides the bounded backstop <see cref="RetryPolicy"/> applied to LLM-call (and related)
/// activities when the user has NOT configured one.
/// </summary>
/// <remarks>
/// <para>
/// A <see langword="null"/> <see cref="ActivityOptions.RetryPolicy"/> is transmitted to the
/// Temporal server as "use the server default" — <c>MaximumAttempts = 0</c>, i.e. <em>unlimited</em>
/// retries. Combined with an LLM error that never recovers (a deterministic 4xx, or a scripted /
/// exhausted client), that means the activity retries forever and the workflow hangs.
/// </para>
/// <para>
/// This bounded default (<c>MaximumAttempts = 5</c>) is the backstop for any error NOT positively
/// classified by <see cref="LlmErrorClassifier"/>: even a "retryable-looking" but permanently broken
/// error terminates after 5 attempts instead of looping. Fail-fast classification handles the known
/// deterministic errors on the first attempt; this handles everything else.
/// </para>
/// </remarks>
internal static class DefaultRetryPolicy
{
    /// <summary>
    /// The default maximum number of activity attempts when the user configured no retry policy.
    /// </summary>
    internal const int DefaultMaximumAttempts = 5;

    /// <summary>
    /// Returns <paramref name="configured"/> when the user set an explicit policy; otherwise a
    /// bounded default (<see cref="DefaultMaximumAttempts"/> attempts) instead of <see langword="null"/>
    /// (which the server treats as unlimited retries).
    /// </summary>
    /// <param name="configured">The user-configured policy, or <see langword="null"/> when unset.</param>
    internal static RetryPolicy Resolve(RetryPolicy? configured) =>
        configured ?? new RetryPolicy { MaximumAttempts = DefaultMaximumAttempts };
}
