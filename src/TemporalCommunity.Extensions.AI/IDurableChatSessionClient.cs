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
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="correlationId">
    /// Optional caller-supplied correlation ID for this turn. When null/empty, the
    /// workflow auto-generates one via <c>Workflow.NewGuid()</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response entry from the LLM, including per-turn <see cref="UsageDetails"/>.</returns>
    Task<DurableSessionResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string? correlationId = null,
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
    /// Submits a human decision for a pending tool approval request, unblocking the workflow.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="decision">The human's approval or rejection decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubmitApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a graceful shutdown signal to the session workflow, causing it to exit its
    /// session loop rather than sitting parked until the configured <c>SessionTimeToLive</c>.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Equivalent to signalling <see cref="DurableChatWorkflowBase{TOutput}.ShutdownSignalName"/>
    /// on the workflow handle, but keeps the caller free of the raw workflow handle and
    /// workflow ID prefix details.
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
    string GetWorkflowId(string conversationId);
}
