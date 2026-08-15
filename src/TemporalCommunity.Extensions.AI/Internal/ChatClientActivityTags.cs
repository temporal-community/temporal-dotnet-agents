using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TemporalCommunity.Extensions.AI.Internal;

/// <summary>Applies durable per-call tag metadata to the current model-activity span.</summary>
internal static class ChatClientActivityTags
{
    private static int _missingActivityWarned;

    internal static void Apply(ChatOptions? options, ILogger logger)
    {
        var tags = options.GetChatClientTags();
        if (tags.Count == 0)
        {
            return;
        }

        var current = Activity.Current;
        if (current is null)
        {
            if (Interlocked.Exchange(ref _missingActivityWarned, 1) == 0)
            {
                logger.LogChatClientTagsSkipped(string.Join(", ", tags.Select(tag => tag.Key)));
            }

            return;
        }

        foreach (var tag in tags)
        {
            current.SetTag(tag.Key, tag.Value);
        }
    }
}
