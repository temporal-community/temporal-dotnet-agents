using TemporalCommunity.Extensions.AI;
using Temporalio.Activities;

namespace ScheduledChat;

public sealed class ScheduledTurnActivities(DurableChatSessionClient sessions)
{
    [Activity("ScheduledChat.SubmitTurn")]
    public async Task<ScheduledTurnReceipt> SubmitAsync(ScheduledTurnOccurrence occurrence)
    {
        var response = await sessions.SendAsync(
            occurrence.ConversationId,
            occurrence.Prompt,
            requestId: occurrence.RequestId,
            cancellationToken: ActivityExecutionContext.Current.CancellationToken);
        return new ScheduledTurnReceipt(occurrence.RequestId, response.Text);
    }
}
