using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>Builds safe model-facing history for terminal managed-loop outcomes.</summary>
internal static class DurableManagedLoopHistory
{
    internal const string IncompleteResponseSentinelText =
        "The model did not produce a complete final response; " +
        "no final assistant answer is available for this turn.";

    internal static ChatResponse ForIncompleteResponse(ChatResponse diagnosticResponse) =>
        new(new ChatMessage(ChatRole.Assistant, IncompleteResponseSentinelText))
        {
            Usage = diagnosticResponse.Usage,
            FinishReason = diagnosticResponse.FinishReason,
        };
}
