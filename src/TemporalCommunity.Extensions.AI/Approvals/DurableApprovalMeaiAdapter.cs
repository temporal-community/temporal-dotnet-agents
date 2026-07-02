using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Approvals;

/// <summary>
/// Adapter methods that map between TemporalCommunity's approval types and
/// Microsoft.Extensions.AI's tool-approval content types.
/// </summary>
/// <remarks>
/// <para>
/// Use these when building a MEAI-aware approval UI that needs to render a
/// <see cref="DurableApprovalRequest"/> as a <see cref="ToolApprovalRequestContent"/>
/// (for display) or convert a user's <see cref="ToolApprovalResponseContent"/> back
/// into a <see cref="DurableApprovalDecision"/> for submission.
/// </para>
/// <para>
/// The adapter is intentionally NOT wired into <c>DurableChatSessionClient</c> or
/// <c>ITemporalAgentClient</c> as built-in overloads — it is a utility you call
/// manually in your UI layer, keeping the core send path free of MEAI approval
/// coupling.
/// </para>
/// <para>
/// <b>Scope limitation:</b> <see cref="ToolApprovalResponseContent"/> has no concept
/// of <see cref="ApprovalScope"/> or <see cref="ApprovalScopePattern"/>. Decisions
/// produced by <see cref="ToDurableDecision"/> always carry
/// <see cref="ApprovalScope.ThisCallOnly"/> and a null <c>ScopePattern</c>. Callers
/// that need session-wide or always-approve behaviour must construct a
/// <see cref="DurableApprovalDecision"/> directly and set the <c>Scope</c> and
/// <c>ScopePattern</c> properties before submitting.
/// </para>
/// </remarks>
[Experimental("TAI001")]
public static class DurableApprovalMeaiAdapter
{
    /// <summary>
    /// Converts a <see cref="DurableApprovalRequest"/> to a
    /// <see cref="ToolApprovalRequestContent"/> so that MEAI-aware UIs can render the
    /// approval request without depending on Temporal types directly.
    /// </summary>
    /// <param name="request">The pending approval request from Temporal.</param>
    /// <returns>
    /// A <see cref="ToolApprovalRequestContent"/> whose <c>RequestId</c> matches
    /// <paramref name="request"/>.<see cref="DurableApprovalRequest.RequestId"/> and
    /// whose <c>ToolCall</c> is a <see cref="FunctionCallContent"/> built from the
    /// request's <see cref="DurableApprovalRequest.CallId"/> and
    /// <see cref="DurableApprovalRequest.FunctionName"/>.
    /// </returns>
    public static ToolApprovalRequestContent ToMeaiRequest(this DurableApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolCall = new FunctionCallContent(
            callId: request.CallId ?? request.RequestId ?? string.Empty,
            name:   request.FunctionName ?? string.Empty);

        return new ToolApprovalRequestContent(
            requestId: request.RequestId ?? string.Empty,
            toolCall:  toolCall);
    }

    /// <summary>
    /// Converts a <see cref="ToolApprovalResponseContent"/> to a
    /// <see cref="DurableApprovalDecision"/> ready for submission to
    /// <c>DurableChatSessionClient.SubmitApprovalAsync</c> or
    /// <c>ITemporalAgentClient.SubmitApprovalAsync</c>.
    /// </summary>
    /// <param name="response">The MEAI approval response from the UI or approver.</param>
    /// <returns>
    /// A <see cref="DurableApprovalDecision"/> with
    /// <see cref="DurableApprovalDecision.Scope"/> set to
    /// <see cref="ApprovalScope.ThisCallOnly"/>. The <c>ScopePattern</c> field is
    /// always <see langword="null"/>. See <see cref="DurableApprovalMeaiAdapter"/> remarks
    /// for guidance when session-scope or always-approve behaviour is required.
    /// </returns>
    public static DurableApprovalDecision ToDurableDecision(this ToolApprovalResponseContent response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new DurableApprovalDecision
        {
            RequestId = response.RequestId,
            Approved  = response.Approved,
            Reason    = response.Reason,
            Scope     = ApprovalScope.ThisCallOnly,
        };
    }
}
