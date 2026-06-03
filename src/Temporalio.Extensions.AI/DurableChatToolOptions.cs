using Temporalio.Common;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Per-tool Temporal activity overrides applied when a durable chat session client
/// dispatches a tool call as a Temporal activity
/// (<c>Temporalio.Extensions.AI.InvokeFunction</c>) under Pattern 3 (durable tool
/// dispatch without a custom workflow).
/// </summary>
/// <remarks>
/// <para>
/// Each property is independent of the others — leave a field <see langword="null"/>
/// to inherit the worker-level default from <see cref="DurableExecutionOptions"/>
/// (<see cref="DurableExecutionOptions.ActivityTimeout"/>,
/// <see cref="DurableExecutionOptions.HeartbeatTimeout"/>,
/// <see cref="DurableExecutionOptions.RetryPolicy"/>).
/// </para>
/// <para>
/// Use <see cref="NoRetry"/> on write-style tools (send email, persist a record,
/// charge a card) so non-idempotent re-execution does not occur on activity retry.
/// Read-style tools generally inherit the default retry policy.
/// </para>
/// <para>
/// This type mirrors <c>Temporalio.Extensions.Agents.DurableToolOptions</c> verbatim
/// for cross-library symmetry.
/// </para>
/// </remarks>
public sealed class DurableChatToolOptions
{
    /// <summary>
    /// Gets or sets the Temporal <c>StartToCloseTimeout</c> applied to this tool's
    /// activity dispatch. When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public TimeSpan? StartToCloseTimeout { get; set; }

    /// <summary>
    /// Gets or sets the Temporal heartbeat timeout applied to this tool's activity
    /// dispatch. When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>
    /// Gets or sets the retry policy applied to this tool's activity dispatch.
    /// When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets a value indicating whether the configured
    /// <see cref="IDurableToolInterceptor{TContext}"/> should be skipped for this tool.
    /// When <see langword="true"/>, the interceptor activity is not dispatched and the
    /// tool proceeds directly to <c>InvokeFunction</c>. Default is <see langword="false"/>.
    /// </summary>
    public bool SkipInterceptorFlag { get; private set; }

    /// <summary>
    /// Gets or sets the timeout budget for this tool's <c>RunToolInterceptor</c> activity.
    /// Independent of <see cref="StartToCloseTimeout"/> (which governs the tool activity itself).
    /// When <see langword="null"/>, the worker-level default activity timeout is used.
    /// </summary>
    public TimeSpan? InterceptorTimeout { get; set; }

    /// <summary>
    /// Gets a value indicating whether a human approval is always required before this tool
    /// is dispatched, regardless of what the <see cref="IDurableToolInterceptor{TContext}"/> returns.
    /// This is Rule 2 — an absolute configuration-time floor.
    /// </summary>
    public bool RequireApprovalFlag { get; private set; }

    /// <summary>
    /// Disables retries for this tool by setting <see cref="RetryPolicy"/> to a policy
    /// with <see cref="RetryPolicy.MaximumAttempts"/> equal to <c>1</c>.
    /// </summary>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <remarks>
    /// Use for non-idempotent / write-style tools so a transient activity failure does
    /// not cause double-execution (e.g. sending the same email twice).
    /// </remarks>
    public DurableChatToolOptions NoRetry()
    {
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        return this;
    }

    /// <summary>
    /// Sets <see cref="RetryPolicy"/> to a policy with
    /// <see cref="RetryPolicy.MaximumAttempts"/> equal to <paramref name="maxAttempts"/>.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts; must be greater than zero.</param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxAttempts"/> is less than or equal to zero.
    /// </exception>
    public DurableChatToolOptions WithMaxAttempts(int maxAttempts)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "Maximum attempts must be greater than zero.");
        }

        RetryPolicy = new RetryPolicy { MaximumAttempts = maxAttempts };
        return this;
    }

    /// <summary>
    /// Sets <see cref="StartToCloseTimeout"/> to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">
    /// The activity start-to-close timeout; must be greater than <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public DurableChatToolOptions WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Timeout must be greater than zero.");
        }

        StartToCloseTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Opts this tool out of the configured <see cref="IDurableToolInterceptor{TContext}"/>.
    /// The <c>RunToolInterceptor</c> activity is not dispatched for this tool; it proceeds
    /// directly to <c>InvokeFunction</c>.
    /// </summary>
    /// <returns>This instance, for fluent chaining.</returns>
    public DurableChatToolOptions SkipInterceptor()
    {
        SkipInterceptorFlag = true;
        return this;
    }

    /// <summary>
    /// Sets the timeout budget for this tool's <c>RunToolInterceptor</c> activity.
    /// </summary>
    /// <param name="timeout">
    /// The interceptor activity start-to-close timeout; must be greater than
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public DurableChatToolOptions WithInterceptorTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Interceptor timeout must be greater than zero.");
        }

        InterceptorTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Marks this tool as always requiring human approval before dispatch, regardless of
    /// what the <see cref="IDurableToolInterceptor{TContext}"/> returns. This is the absolute
    /// configuration-time floor (Rule 2): even if the interceptor returns <c>Proceed</c>,
    /// the turn loop will pause for approval using the interceptor's <c>EnrichedDescription</c>
    /// (if any).
    /// </summary>
    /// <returns>This instance, for fluent chaining.</returns>
    public DurableChatToolOptions RequireApproval()
    {
        RequireApprovalFlag = true;
        return this;
    }
}
