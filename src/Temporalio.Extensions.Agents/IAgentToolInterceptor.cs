namespace Temporalio.Extensions.Agents;

/// <summary>
/// Pre-tool lifecycle hook that fires as a dedicated Temporal activity before
/// <c>InvokeAgentTool</c> is dispatched for each tool call in a turn.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface to enrich approval context, apply risk scoring, write pre-execution
/// audit records, perform argument transformation, or short-circuit tool dispatch entirely —
/// all without modifying individual tool implementations.
/// </para>
/// <para>
/// <b>Execution model.</b> One <c>RunToolInterceptor</c> activity is dispatched per tool call,
/// fan-out in parallel via <c>Workflow.WhenAllAsync</c>, <em>before</em> any
/// <c>InvokeAgentTool</c> activity is dispatched. The interceptor result for each tool is
/// recorded in Temporal history; on replay the activity is not re-executed.
/// </para>
/// <para>
/// <b>Registration.</b> Register per-agent via
/// <see cref="DurableAgentBuilder.AddToolInterceptor(Func{IServiceProvider, IAgentToolInterceptor})"/>
/// or as a worker-level default via <see cref="TemporalAgentsOptions.DefaultToolInterceptor"/>.
/// The H1 rule applies: per-agent registration wins over worker default.
/// </para>
/// <para>
/// <b>AfterToolCallAsync</b> is named and reserved for a follow-on release. When it ships,
/// the interface will gain a default-interface-method implementation so existing interceptors
/// are not broken.
/// </para>
/// </remarks>
public interface IAgentToolInterceptor
{
    /// <summary>
    /// Called before each tool activity is dispatched. Return an <see cref="AgentToolDecision"/>
    /// to control whether the tool runs, is skipped, is blocked, or requires human approval.
    /// </summary>
    /// <param name="context">Describes the tool call about to be dispatched.</param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    /// <returns>
    /// An <see cref="AgentToolDecision"/> that controls how the turn loop handles the tool call.
    /// </returns>
    Task<AgentToolDecision> BeforeToolCallAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);
}
