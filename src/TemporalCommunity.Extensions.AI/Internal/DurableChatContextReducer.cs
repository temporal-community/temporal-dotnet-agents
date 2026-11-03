#pragma warning disable MEAI001 // IChatReducer is experimental in the pinned MEAI version.
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI.Internal;

internal sealed record DurableChatContextProjection(
    IReadOnlyList<ChatMessage> Messages,
    int CurrentTurnStartIndex,
    IReadOnlyList<DurableChatTurnRange> PreviousTurnRanges);

internal static class DurableChatContextReducer
{
    private static readonly ChatRole s_developerRole = new("developer");

    public static async Task<DurableChatContextProjection> ReduceAsync(
        IChatReducer reducer,
        IReadOnlyList<ChatMessage> messages,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(previousTurnRanges);

        ValidateRanges(messages.Count, currentTurnStartIndex, previousTurnRanges);

        var reducedMessages = new List<ChatMessage>(messages.Count);
        var reducedRanges = new List<DurableChatTurnRange>(previousTurnRanges.Count);
        var inputIndex = 0;
        var rangeIndex = 0;

        while (rangeIndex < previousTurnRanges.Count)
        {
            var range = previousTurnRanges[rangeIndex];
            AppendProtectedGap(inputIndex, range.StartIndex);

            if (IsEligible(range, messages))
            {
                var batchStartIndex = range.StartIndex;
                var batchEndIndex = range.EndIndex;
                var nextRangeIndex = rangeIndex + 1;
                while (nextRangeIndex < previousTurnRanges.Count)
                {
                    var next = previousTurnRanges[nextRangeIndex];
                    if (next.StartIndex != batchEndIndex || !IsEligible(next, messages))
                    {
                        break;
                    }

                    batchEndIndex = next.EndIndex;
                    nextRangeIndex++;
                }

                var reducerInput = CloneMessages(messages, batchStartIndex, batchEndIndex);
                var reducerOutput = await reducer
                    .ReduceAsync(reducerInput, cancellationToken)
                    .ConfigureAwait(false);
                var outputStartIndex = reducedMessages.Count;
                reducedMessages.AddRange(CloneMessages(reducerOutput.ToList()));
                if (reducedMessages.Count > outputStartIndex)
                {
                    reducedRanges.Add(new DurableChatTurnRange(
                        outputStartIndex,
                        reducedMessages.Count,
                        IsProtected: true));
                }

                inputIndex = batchEndIndex;
                rangeIndex = nextRangeIndex;
                continue;
            }

            AppendProtectedRange(range);
            inputIndex = range.EndIndex;
            rangeIndex++;
        }

        AppendProtectedGap(inputIndex, currentTurnStartIndex);
        var reducedCurrentTurnStartIndex = reducedMessages.Count;
        reducedMessages.AddRange(messages.Skip(currentTurnStartIndex));

        ValidateToolGrammar(reducedMessages);
        return new DurableChatContextProjection(
            reducedMessages,
            reducedCurrentTurnStartIndex,
            reducedRanges);

        void AppendProtectedGap(int startIndex, int endIndex)
        {
            if (endIndex <= startIndex)
            {
                return;
            }

            var outputStartIndex = reducedMessages.Count;
            reducedMessages.AddRange(messages.Skip(startIndex).Take(endIndex - startIndex));
            reducedRanges.Add(new DurableChatTurnRange(
                outputStartIndex,
                reducedMessages.Count,
                IsProtected: true));
        }

        void AppendProtectedRange(DurableChatTurnRange range)
        {
            var outputStartIndex = reducedMessages.Count;
            reducedMessages.AddRange(
                messages.Skip(range.StartIndex).Take(range.EndIndex - range.StartIndex));
            if (reducedMessages.Count > outputStartIndex)
            {
                reducedRanges.Add(new DurableChatTurnRange(
                    outputStartIndex,
                    reducedMessages.Count,
                    IsProtected: true));
            }
        }
    }

    private static bool IsEligible(
        DurableChatTurnRange range,
        IReadOnlyList<ChatMessage> messages)
    {
        if (range.IsProtected
            || messages.Skip(range.StartIndex).Take(range.EndIndex - range.StartIndex)
                .Any(message => message.Role == ChatRole.System || message.Role == s_developerRole))
        {
            return false;
        }

        return true;
    }

    private static List<ChatMessage> CloneMessages(
        IReadOnlyList<ChatMessage> messages,
        int startIndex,
        int endIndex) =>
        CloneMessages(messages.Skip(startIndex).Take(endIndex - startIndex).ToList());

    private static List<ChatMessage> CloneMessages(IReadOnlyList<ChatMessage> messages)
    {
        var cloned = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw new DurableContextReductionException(
                    "The chat reducer returned a null message.");
            }
            var element = JsonSerializer.SerializeToElement(
                message,
                DurableAIJsonUtilities.DefaultOptions);
            cloned.Add(element.Deserialize<ChatMessage>(DurableAIJsonUtilities.DefaultOptions)
                ?? throw new DurableContextReductionException(
                    "The chat reducer produced a message that could not be cloned."));
        }

        return cloned;
    }

    private static void ValidateRanges(
        int messageCount,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> ranges)
    {
        if (currentTurnStartIndex < 0 || currentTurnStartIndex > messageCount)
        {
            throw new DurableContextReductionException(
                "The model-context reducer received an invalid current-turn boundary.");
        }

        var previousEnd = 0;
        foreach (var range in ranges)
        {
            if (range.StartIndex < previousEnd
                || range.StartIndex < 0
                || range.EndIndex <= range.StartIndex
                || range.EndIndex > currentTurnStartIndex)
            {
                throw new DurableContextReductionException(
                    "The model-context reducer received invalid prior-turn boundaries.");
            }

            previousEnd = range.EndIndex;
        }
    }

    private static void ValidateToolGrammar(IReadOnlyList<ChatMessage> messages)
    {
        var pendingCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        if (string.IsNullOrEmpty(call.CallId) || !pendingCallIds.Add(call.CallId))
                        {
                            throw new DurableContextReductionException(
                                "The chat reducer returned a duplicate or unidentified tool call.");
                        }
                        break;
                    case FunctionResultContent result:
                        if (string.IsNullOrEmpty(result.CallId) || !pendingCallIds.Remove(result.CallId))
                        {
                            throw new DurableContextReductionException(
                                "The chat reducer returned an orphaned or duplicate tool result.");
                        }
                        break;
                }
            }
        }

        if (pendingCallIds.Count > 0)
        {
            throw new DurableContextReductionException(
                "The chat reducer returned an unresolved tool call.");
        }
    }
}

internal sealed class DurableContextReductionException(string message) : Exception(message);
