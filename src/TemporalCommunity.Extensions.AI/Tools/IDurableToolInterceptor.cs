namespace TemporalCommunity.Extensions.AI.Tools;

/// <summary>
/// Pre-tool lifecycle hook. Fires as a Temporal activity before a durable tool is
/// dispatched. Implement this interface to apply policy, enrich approval context, score risk,
/// transform arguments, or short-circuit tool execution entirely.
/// </summary>
/// <typeparam name="TContext">
/// The context type supplied to <see cref="BeforeToolCallAsync"/>. Must be or extend
/// <see cref="DurableToolContext"/>. Use <see cref="DurableToolContext"/> directly for the
/// standard durable chat pipeline, or derive a context type for application-specific fields.
/// </typeparam>
/// <remarks>
/// <para>
/// The <c>in</c> variance annotation means an <c>IDurableToolInterceptor&lt;DurableToolContext&gt;</c>
/// can be assigned to any <c>IDurableToolInterceptor&lt;TContext&gt;</c> variable where
/// <c>TContext</c> derives from <c>DurableToolContext</c>. This allows an interceptor that accepts
/// the base context to work with pipelines that supply a derived context.
/// </para>
/// </remarks>
public interface IDurableToolInterceptor<in TContext>
    where TContext : DurableToolContext
{
    /// <summary>
    /// Called before a tool activity is dispatched.
    /// </summary>
    /// <param name="context">Describes the tool call about to be dispatched.</param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    /// <returns>
    /// A <see cref="DurableToolDecision"/> that controls how the dispatch loop handles the tool call.
    /// </returns>
    Task<DurableToolDecision> BeforeToolCallAsync(
        TContext context,
        CancellationToken cancellationToken);
}
