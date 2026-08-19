using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Session;

namespace TemporalCommunity.Extensions.Agents;

/// <summary>
/// Opt-in administrative capability for reusable approval grants within one agent session.
/// </summary>
/// <remarks>
/// This service is not registered by normal agent setup. Register it explicitly with
/// <c>AddTemporalAgentApprovalScopeAdministration()</c> only in a trusted backend. Possession of
/// this service is a capability, not authentication; callers must authorize the application
/// resource before selecting a session ID.
/// </remarks>
public interface ITemporalAgentApprovalScopeAdministration
{
    /// <summary>Approves the pending call and creates an expiring session grant.</summary>
    Task<SessionApprovalScopeGrantResult> GrantSessionScopeAsync(
        TemporalAgentSessionId sessionId,
        SessionApprovalScopeGrantRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes a session grant by its stable grant identifier.</summary>
    Task<bool> RevokeSessionScopeAsync(
        TemporalAgentSessionId sessionId,
        string grantId,
        CancellationToken cancellationToken = default);
}
