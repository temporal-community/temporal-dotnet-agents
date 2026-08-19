using Temporalio.Common;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>
/// Provides workload-appropriate bounded <see cref="RetryPolicy"/> defaults when the user has
/// not configured one.
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
    /// <remarks>
    /// The bounded default also caps the inter-attempt backoff at
    /// <see cref="DefaultModelMaximumIntervalSeconds"/> seconds (vs the server default of 100s). Bounding
    /// both the attempt <em>count</em> and the backoff <em>interval</em> keeps a permanently-failing
    /// LLM step from taking minutes to surface its terminal failure — the whole point of the
    /// hardening is that the caller's <c>SendAsync</c> returns promptly instead of hanging.
    /// </remarks>
    /// <param name="configured">The user-configured policy, or <see langword="null"/> when unset.</param>
    internal static RetryPolicy ResolveForModel(RetryPolicy? configured) =>
        configured ?? new RetryPolicy
        {
            MaximumAttempts = DefaultMaximumAttempts,
            MaximumInterval = TimeSpan.FromSeconds(DefaultModelMaximumIntervalSeconds),
        };

    /// <summary>
    /// Returns <paramref name="configured"/> unchanged when supplied; otherwise returns the
    /// bounded default used for tool and policy activities.
    /// </summary>
    /// <remarks>
    /// Tool work gets a longer maximum backoff than an interactive model call so a transient
    /// dependency has a meaningful recovery window. This is not a total execution budget and
    /// does not interpret provider-specific retry-after headers.
    /// </remarks>
    internal static RetryPolicy ResolveForTool(RetryPolicy? configured) =>
        configured ?? new RetryPolicy
        {
            MaximumAttempts = DefaultMaximumAttempts,
            MaximumInterval = TimeSpan.FromSeconds(DefaultToolMaximumIntervalSeconds),
        };

    /// <summary>
    /// Caps the inter-attempt backoff for the bounded model default policy (seconds).
    /// </summary>
    internal const int DefaultModelMaximumIntervalSeconds = 2;

    /// <summary>
    /// Caps the inter-attempt backoff for the bounded tool default policy (seconds).
    /// </summary>
    internal const int DefaultToolMaximumIntervalSeconds = 30;
}
