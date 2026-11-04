using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableHistoryProjectionValidator
{
    internal static List<DurableSessionEntry> Validate(
        IReadOnlyList<DurableSessionEntry> source,
        IReadOnlyList<DurableSessionEntry> projection)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projection);

        var sourceIndices = new Dictionary<EntryIdentity, int>();
        for (var index = 0; index < source.Count; index++)
        {
            if (!sourceIndices.TryAdd(EntryIdentity.From(source[index]), index))
            {
                throw InvalidProjection("Source history contains ambiguous entry identities.");
            }
        }

        var projectedIndices = new HashSet<int>();
        var canonicalProjection = new List<DurableSessionEntry>(projection.Count);
        var priorIndex = -1;
        foreach (var entry in projection)
        {
            if (entry is null || !sourceIndices.TryGetValue(EntryIdentity.From(entry), out var sourceIndex))
            {
                throw InvalidProjection("A reducer may only retain original history entries.");
            }
            if (sourceIndex <= priorIndex || !projectedIndices.Add(sourceIndex))
            {
                throw InvalidProjection("Reducer output must preserve original order without duplicates.");
            }
            priorIndex = sourceIndex;
            canonicalProjection.Add(source[sourceIndex]);
        }

        foreach (var (firstIndex, lastIndex, isProtected) in GetTurnRanges(source))
        {
            var firstRetained = projectedIndices.Contains(firstIndex);
            var lastRetained = projectedIndices.Contains(lastIndex);
            if (firstRetained != lastRetained)
            {
                throw InvalidProjection("Reducer output split a request/response turn.");
            }
            if (isProtected && !firstRetained)
            {
                throw InvalidProjection("Reducer output removed protected current, instruction, approval, or interrupted history.");
            }
        }

        return canonicalProjection;
    }

    private static IEnumerable<(int FirstIndex, int LastIndex, bool IsProtected)> GetTurnRanges(
        IReadOnlyList<DurableSessionEntry> history)
    {
        var groups = new List<(int FirstIndex, int LastIndex, bool IsProtected)>();
        for (var index = 0; index < history.Count; index++)
        {
            if (history[index] is DurableSessionRequest request
                && index + 1 < history.Count
                && history[index + 1] is DurableSessionResponse response
                && SameTurn(request, response))
            {
                groups.Add((
                    index,
                    index + 1,
                    response.CompletionReason == DurableTurnCompletionReason.Interrupted
                        || ContainsInstructionContent(request.Messages)
                        || ContainsInstructionContent(response.Messages)
                        || ContainsApprovalContent(request.Messages)
                        || ContainsApprovalContent(response.Messages)));
                index++;
            }
            else if (history[index] is DurableSessionRequest or DurableSessionResponse)
            {
                groups.Add((index, index, IsProtected: true));
            }
        }

        var latestCompleteTurn = groups.FindLastIndex(group => group.FirstIndex != group.LastIndex);
        if (latestCompleteTurn >= 0)
        {
            var latest = groups[latestCompleteTurn];
            groups[latestCompleteTurn] = (latest.FirstIndex, latest.LastIndex, true);
        }

        return groups;
    }

    private static bool SameTurn(DurableSessionRequest request, DurableSessionResponse response) =>
        request.TurnId is not null && response.TurnId is not null
            ? string.Equals(request.TurnId, response.TurnId, StringComparison.Ordinal)
            : string.Equals(request.CorrelationId, response.CorrelationId, StringComparison.Ordinal);

    private static bool ContainsApprovalContent(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents)
            .Any(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent);

    private static bool ContainsInstructionContent(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer"));

    private static DurableHistoryProjectionException InvalidProjection(string reason) =>
        new(reason);

    private readonly record struct EntryIdentity(
        string? EntryId,
        Type EntryType,
        string CorrelationId,
        DateTimeOffset CreatedAt)
    {
        internal static EntryIdentity From(DurableSessionEntry entry) =>
            new(entry.EntryId, entry.GetType(), entry.CorrelationId, entry.CreatedAt);
    }
}
