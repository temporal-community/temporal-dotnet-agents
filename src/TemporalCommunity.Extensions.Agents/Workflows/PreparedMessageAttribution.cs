using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed record PreparedMessageAttribution(
    int MessageIndex,
    string SourceType,
    string? SourceId)
{
    internal static IReadOnlyList<PreparedMessageAttribution> Capture(
        IReadOnlyList<ChatMessage> messages)
    {
        var attributions = new List<PreparedMessageAttribution>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var sourceType = message.GetAgentRequestMessageSourceType();
            var sourceId = message.GetAgentRequestMessageSourceId();
            if (sourceType != AgentRequestMessageSourceType.External || sourceId is not null)
            {
                attributions.Add(new PreparedMessageAttribution(index, sourceType.Value, sourceId));
            }
        }

        return attributions;
    }

    internal static IReadOnlyList<ChatMessage> Restore(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<PreparedMessageAttribution>? attributions)
    {
        if (attributions is null or { Count: 0 })
        {
            return messages;
        }

        var restored = messages.ToArray();
        foreach (var attribution in attributions)
        {
            if ((uint)attribution.MessageIndex >= (uint)restored.Length)
            {
                throw new InvalidOperationException(
                    $"Prepared message attribution index {attribution.MessageIndex} is outside the message list.");
            }

            restored[attribution.MessageIndex] = restored[attribution.MessageIndex]
                .WithAgentRequestMessageSource(
                    new AgentRequestMessageSourceType(attribution.SourceType),
                    attribution.SourceId);
        }

        return restored;
    }
}
