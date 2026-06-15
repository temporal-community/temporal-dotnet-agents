using Temporalio.Extensions.AI.Tools;

namespace Temporalio.Extensions.Agents.Tools;

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
/// This interface is a convenience alias for
/// <see cref="IDurableToolInterceptor{TContext}"><c>IDurableToolInterceptor&lt;AgentToolContext&gt;</c></see>.
/// Implementors return <see cref="DurableToolDecision"/> (defined in
/// <c>Temporalio.Extensions.AI</c>) from <c>BeforeToolCallAsync</c>.
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
public interface IAgentToolInterceptor : IDurableToolInterceptor<AgentToolContext>
{
    // BeforeToolCallAsync is inherited from IDurableToolInterceptor<AgentToolContext>:
    //   Task<DurableToolDecision> BeforeToolCallAsync(AgentToolContext context, CancellationToken cancellationToken)
}
