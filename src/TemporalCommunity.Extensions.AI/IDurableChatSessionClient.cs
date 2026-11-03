using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Abstraction over a durable chat session backed by a Temporal workflow.
/// </summary>
/// <remarks>
/// <para>
/// The concrete implementation is <see cref="DurableChatSessionClient"/>, which maps each
/// <c>conversationId</c> to a long-lived Temporal workflow and delivers chat turns
/// via <c>[WorkflowUpdate]</c>.
/// </para>
/// <para>
/// This interface exists so that application code (controllers, background services, etc.)
/// that depends on the session client can be tested with a simple test double — without
/// spinning up a Temporal worker or using <c>WorkflowEnvironment</c>. The concrete class
/// itself should be tested via integration tests.
/// </para>
/// </remarks>
public interface IDurableChatSessionClient
{
    /// <summary>
    /// Sends messages to a durable chat session and returns the response entry.
    /// Starts the session workflow if not already running.
    /// </summary>
    /// <remarks>
    /// Retrying after an ambiguous timeout or lost response can submit another turn (at-least-once
    /// submission). Correlation IDs provide observability, not deduplication. Effectful tools need
    /// downstream business idempotency to suppress repeated external effects.
    /// <see cref="ChatOptions.ConversationId"/>, <see cref="ChatOptions.RawRepresentationFactory"/>,
    /// and <see cref="ChatOptions.ContinuationToken"/> are rejected because provider-owned
    /// conversation state cannot be resumed across durable activity boundaries.
    /// </remarks>
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="correlationId">
    /// An observability identifier, not an idempotency key; reusing it does not deduplicate turns.
    /// Optional caller-supplied correlation ID for this turn. When null/empty, the
    /// workflow auto-generates one via <c>Workflow.NewGuid()</c>.
    /// </param>
    /// <param name="requestId">
    /// Optional caller-known logical request ID. Reuse it only when retrying the same turn with
    /// identical effect-bearing input; correlation IDs and Temporal update IDs are separate.
    /// </param>
    /// <param name="expectedSessionEpoch">
    /// Optional session-lineage precondition. A mismatch fails with
    /// <see cref="Exceptions.DurableChatSessionEpochMismatchException"/> before turn admission.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The response entry from the LLM, including per-turn <see cref="UsageDetails"/>, provider
    /// response/model IDs, finish reason, and serializable response metadata.
    /// </returns>
    Task<DurableSessionResponse> SendAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string? correlationId = null,
        string? requestId = null,
        string? expectedSessionEpoch = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one user text message to a durable chat session.
    /// </summary>
    /// <param name="conversationId">A unique application conversation identifier.</param>
    /// <param name="message">User text to append to the conversation.</param>
    /// <param name="options">Optional supported MEAI chat options.</param>
    /// <param name="correlationId">Optional observability identifier for the turn.</param>
    /// <param name="requestId">
    /// Optional caller-known logical request ID. Reuse it only when retrying the same turn with
    /// identical effect-bearing input.
    /// </param>
    /// <param name="expectedSessionEpoch">
    /// Optional session-lineage precondition. A mismatch fails with
    /// <see cref="Exceptions.DurableChatSessionEpochMismatchException"/> before turn admission.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for this RPC wait.</param>
    /// <returns>
    /// The durable response entry for the turn, including provider response/model IDs, finish
    /// reason, and serializable response metadata.
    /// </returns>
    Task<DurableSessionResponse> SendAsync(
        string conversationId,
        string message,
        ChatOptions? options = null,
        string? correlationId = null,
        string? requestId = null,
        string? expectedSessionEpoch = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the opaque epoch identifying the current session lineage.
    /// </summary>
    /// <param name="conversationId">The application conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token for this query.</param>
    Task<string> GetSessionEpochAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full conversation history persisted in the session workflow.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// All <see cref="DurableSessionEntry"/> instances accumulated in the workflow. Each turn
    /// produces a request entry followed by a response entry.
    /// </returns>
    Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the currently pending tool approval request for a session, or <see langword="null"/>
    /// if no approval is pending.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a human decision for a pending tool approval request.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="decision">The human's approval or rejection decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DurableApprovalResolutionResult> ResolveApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the workflow's latest event-history usage and admission thresholds.
    /// </summary>
    Task<DurableChatHistoryBudgetStatus> GetHistoryBudgetAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Retries the configured retained-history strategy once for a blocked session.</summary>
    Task<DurableHistoryRecoveryResult> RetryHistoryCompactionAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly trims an oldest complete-turn prefix once for the current blocked incident.
    /// The removed range is recorded with a truncation marker.
    /// </summary>
    Task<DurableHistoryRecoveryResult> TrimBlockedHistoryAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests workflow-owned cancellation of the active turn identified by
    /// <paramref name="requestId"/>.
    /// </summary>
    /// <param name="conversationId">The application conversation identifier.</param>
    /// <param name="requestId">The stable request ID supplied when the turn was started.</param>
    /// <param name="cancellationToken">
    /// Cancellation token for this RPC wait only. It does not cancel an accepted turn.
    /// </param>
    Task<DurableTurnCancellationResult> CancelTurnAsync(
        string conversationId,
        string requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a validated graceful shutdown update to the session workflow, causing it to exit its
    /// session loop rather than sitting parked until the configured <c>SessionTimeToLive</c>.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Only the first request is accepted. Repeated shutdown requests are rejected before they
    /// add another workflow-history event.
    /// </remarks>
    Task ShutdownAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the Temporal workflow ID for a given conversation ID using the configured
    /// <c>WorkflowIdPrefix</c>. Use this when external code needs to address the workflow
    /// directly (e.g. to attach a custom signal) while staying in sync with the session
    /// client's prefix configuration.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <returns>The workflow ID used by the session workflow for this conversation.</returns>
    /// <remarks>
    /// The returned value is a routing locator, not proof that a caller owns or may access the
    /// conversation. Perform application authentication and resource authorization first.
    /// </remarks>
    string GetWorkflowId(string conversationId);
}
