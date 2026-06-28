namespace TemporalCommunity.Extensions.Agents.Approvals;

/// <summary>
/// Durable storage abstraction for always-scope approval records.
/// Used by Feature B (Approval Scopes) to persist and retrieve cross-session scope records
/// for agents configured with <c>UseApprovalScopes()</c> and <see cref="ApprovalScope.Always"/>.
/// </summary>
/// <remarks>
/// <para>
/// This store is separate from <see cref="IAgentHistoryStore"/>, which stores ordered
/// conversation entries keyed by session ID. <see cref="IApprovalScopeStore"/> is a per-agent
/// key/value store for cross-session approval scopes.
/// </para>
/// <para>
/// Methods on this interface are called from Temporal activities. Transient failures are handled
/// automatically by Temporal's activity retry policy — implementations do not need to add their
/// own retry logic.
/// </para>
/// </remarks>
public interface IApprovalScopeStore
{
    /// <summary>
    /// Loads all always-scope records for an agent and logical store key.
    /// </summary>
    /// <param name="agentName">The agent name identifying whose scopes to load.</param>
    /// <param name="storeKey">The logical store key (e.g. <c>"temporal.approval_scopes.always"</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A read-only list of <see cref="ApprovalScopeRecord"/> instances. Returns an empty list
    /// (not <see langword="null"/>) when no records exist for the agent/key pair.
    /// </returns>
    /// <remarks>
    /// This method is called from a Temporal activity. Transient failures are handled
    /// automatically by Temporal's activity retry policy — implementations do not need to
    /// add their own retry logic.
    /// </remarks>
    Task<IReadOnlyList<ApprovalScopeRecord>> LoadAsync(
        string agentName,
        string storeKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a record if <see cref="ApprovalScopeRecord.OriginatingRequestId"/> is not already
    /// present for the agent/key pair. Implementations must make this idempotent.
    /// </summary>
    /// <param name="agentName">The agent name identifying whose scopes to append to.</param>
    /// <param name="storeKey">The logical store key under which to store the record.</param>
    /// <param name="record">The approval scope record to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// This method is called from a Temporal activity. Transient failures are handled
    /// automatically by Temporal's activity retry policy — implementations do not need to
    /// add their own retry logic. The append-if-absent operation must be atomic for a given
    /// <paramref name="agentName"/> and <paramref name="storeKey"/> so concurrent activity retries
    /// or concurrent sessions cannot create duplicate records.
    /// </remarks>
    Task AppendAsync(
        string agentName,
        string storeKey,
        ApprovalScopeRecord record,
        CancellationToken cancellationToken = default);
}
