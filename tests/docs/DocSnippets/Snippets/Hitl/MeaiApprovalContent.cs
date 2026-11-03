// HARNESS for docs/how-to/MEAI/hitl-patterns.md § "MEAI approval content".
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Approvals;

namespace DocSnippets.Hitl;

internal static class MeaiApprovalContent
{
    internal static async Task ResolveAsync(
        DurableChatSessionClient sessionClient,
        string conversationId,
        DurableApprovalRequest pending)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/hitl-patterns.md#meai-approval-content (lines 83-86)
        var meaiRequest = pending.ToMeaiRequest();
        var meaiResponse = meaiRequest.CreateResponse(approved: true, reason: "Reviewed.");
        var decision = meaiResponse.ToDurableDecision();
        await sessionClient.ResolveApprovalAsync(conversationId, decision);
        // END SNIPPET docs/how-to/MEAI/hitl-patterns
    }
}
