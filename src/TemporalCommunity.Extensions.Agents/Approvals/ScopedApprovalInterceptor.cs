using System.Text;
using TemporalCommunity.Extensions.Agents.Tools;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Tools;

namespace TemporalCommunity.Extensions.Agents.Approvals;

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
    /// Decision priority stack (spec Section 5):
    /// <list type="number">
    ///   <item>Tool not scope-aware → <c>Proceed()</c> immediately.</item>
    ///   <item>Session scopes checked via <see cref="ApprovalScopeHelpers.TryMatchScope"/> → <c>Proceed()</c> on match.</item>
    ///   <item>Always-scopes cache (loaded at session start) checked → <c>Proceed()</c> on match.</item>
    ///   <item>No match and <see cref="AgentToolContext.RequiresApproval"/> is true → <c>PauseForApproval()</c>.</item>
    ///   <item>Default → <c>Proceed()</c>.</item>
    /// </list>
    /// </remarks>
    public Task<DurableToolDecision> BeforeToolCallAsync(
        AgentToolContext context, CancellationToken cancellationToken)
    {
        // Step 1: non-scope-aware tools proceed immediately — no scope lookup needed.
        if (!context.ScopeAware)
            return Task.FromResult(DurableToolDecision.Proceed());

        if (context.StateBag is not null)
        {
            // Step 2: check session scopes.
            if (ApprovalScopeHelpers.TryMatchScope(
                context.ToolName,
                context.Arguments,
                context.StateBag,
                "temporal.approval_scopes.session",
                out var sessionMatch))
            {
                return Task.FromResult(DurableToolDecision.Proceed(
                    enrichedDescription: $"Auto-approved by session scope (originally: {sessionMatch!.OriginatingRequestId})"));
            }

            // Step 3: check always-scopes cache (loaded into StateBag at session start).
            if (ApprovalScopeHelpers.TryMatchScope(
                context.ToolName,
                context.Arguments,
                context.StateBag,
                _opts.AlwaysScopesStoreKey,
                out var alwaysMatch))
            {
                return Task.FromResult(DurableToolDecision.Proceed(
                    enrichedDescription: $"Auto-approved by always-scope (originally: {alwaysMatch!.OriginatingRequestId})"));
            }
        }

        // Step 4: no matching scope record found.
        // Scope-aware required tools still pause for human review.
        if (context.RequiresApproval)
        {
            return Task.FromResult(DurableToolDecision.PauseForApproval(
                $"Tool '{context.ToolName}' requires approval. Arguments: {FormatArgs(context.Arguments)}"));
        }

        // Step 5: scope-aware but not required — proceed.
        return Task.FromResult(DurableToolDecision.Proceed());
    }

    private static string FormatArgs(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
            return "{}";

        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kvp in arguments)
        {
            if (!first) sb.Append(", ");
            sb.Append(kvp.Key).Append(": ").Append(kvp.Value ?? "null");
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }
}
