using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Abstraction over a durable chat session backed by a Temporal workflow.
/// </summary>
/// <remarks>
/// <para>
/// The concrete implementation is <see cref="DurableChatSessionClient"/>, which maps each
/// <paramref name="conversationId"/> to a long-lived Temporal workflow and delivers chat turns
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
    /// Sends messages to a durable chat session and returns the response.
    /// Starts the session workflow if not already running.
    /// </summary>
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chat response from the LLM.</returns>
    Task<ChatResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full conversation history persisted in the session workflow.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All messages accumulated in the workflow, including tool calls and results.</returns>
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(
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
    /// <returns>The resolved decision as stored by the workflow.</returns>
    Task<DurableApprovalDecision> SubmitApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);
}
