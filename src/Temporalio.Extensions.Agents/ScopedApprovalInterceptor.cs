using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Built-in interceptor installed by <see cref="DurableAgentBuilder.UseApprovalScopes"/>.
/// Checks session and always-scopes before parking the workflow for human approval;
/// tools that are not scope-annotated or have no matching scope record fall through to
/// the standard <see cref="DurableToolOptions.RequireApproval()"/> gate.
/// </summary>
/// <remarks>
/// This interceptor is managed exclusively by <c>UseApprovalScopes()</c>. Do not instantiate
/// it directly or register it via <see cref="DurableAgentBuilder.AddToolInterceptor"/>.
/// </remarks>
internal sealed class ScopedApprovalInterceptor : IAgentToolInterceptor
{
    private readonly ApprovalScopesOptions _opts;

    /// <summary>
    /// Initializes a new instance of <see cref="ScopedApprovalInterceptor"/> with the given
    /// scope options.
    /// </summary>
    /// <param name="opts">The approval-scopes options configured via <c>UseApprovalScopes()</c>.</param>
    internal ScopedApprovalInterceptor(ApprovalScopesOptions opts)
    {
        _opts = opts;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Full scope-matching behavior is implemented in Task 4.5 (Group 4). This compile-only shell
    /// satisfies the constructor shape required by <see cref="DurableAgentBuilder.UseApprovalScopes"/>.
    /// </remarks>
    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context, CancellationToken cancellationToken)
    {
        // Task 4.5 completes this implementation with full scope-matching logic using
        // ApprovalScopeHelpers.TryMatchScope and the ScopeAware / RequiresApproval context fields
        // added in Group 4. This placeholder is a safe no-op: non-scope-aware tool calls proceed,
        // and scope-aware required tools will be caught by RequiresApprovalTools / GetEffectiveOutcome
        // (which is unchanged until scope-aware exclusion is wired in Group 3).
        return Task.FromResult(DurableToolDecision.Proceed());
    }
}
