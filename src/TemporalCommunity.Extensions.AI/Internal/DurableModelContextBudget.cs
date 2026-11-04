using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;

namespace TemporalCommunity.Extensions.AI.Internal;

internal static class DurableModelContextBudget
{
    internal static IList<ChatMessage> Fit(
        IReadOnlyList<ChatMessage> messages,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges,
        ChatOptions options,
        int? maximumContextTokens,
        int outputTokenReserve,
        IDurableChatTokenEstimator? estimator)
    {
        ValidateArguments(messages, currentTurnStartIndex, previousTurnRanges, maximumContextTokens, outputTokenReserve);
        if (maximumContextTokens is null)
        {
            return messages.ToList();
        }

        var heuristicEstimator = estimator is null
            ? new HeuristicDurableChatTokenEstimator(messages, options)
            : null;
        var estimate = heuristicEstimator?.EstimateFrom(0) ?? Estimate(messages, options, estimator);
        if (Fits(estimate, outputTokenReserve, maximumContextTokens.Value))
        {
            return messages.ToList();
        }

        var dropThrough = 0;
        foreach (var range in previousTurnRanges)
        {
            if (range.StartIndex != dropThrough
                || range.EndIndex <= range.StartIndex
                || range.EndIndex > currentTurnStartIndex
                || range.IsProtected
                || messages.Skip(range.StartIndex).Take(range.EndIndex - range.StartIndex)
                    .Any(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer")))
            {
                break;
            }

            dropThrough = range.EndIndex;
            if (heuristicEstimator is not null)
            {
                estimate = heuristicEstimator.EstimateFrom(dropThrough);
                if (Fits(estimate, outputTokenReserve, maximumContextTokens.Value))
                {
                    return messages.Skip(dropThrough).ToArray();
                }
                continue;
            }

            var candidate = messages.Skip(dropThrough).ToArray();
            estimate = Estimate(candidate, options, estimator);
            if (Fits(estimate, outputTokenReserve, maximumContextTokens.Value))
            {
                return candidate;
            }
        }

        throw new DurableContextBudgetExceededException(
            estimate,
            outputTokenReserve,
            maximumContextTokens.Value);
    }

    internal static void EnsureFits(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        int? maximumContextTokens,
        int outputTokenReserve,
        IDurableChatTokenEstimator? estimator)
    {
        if (maximumContextTokens is null)
        {
            return;
        }

        var estimate = Estimate(messages, options, estimator);
        if (!Fits(estimate, outputTokenReserve, maximumContextTokens.Value))
        {
            throw new DurableContextBudgetExceededException(
                estimate,
                outputTokenReserve,
                maximumContextTokens.Value);
        }
    }

    private static int Estimate(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        IDurableChatTokenEstimator? estimator)
    {
        var estimate = estimator?.EstimateInputTokens(messages, options)
            ?? new HeuristicDurableChatTokenEstimator(messages, options).EstimateFrom(0);
        ArgumentOutOfRangeException.ThrowIfNegative(estimate);
        return estimate;
    }

    private static bool Fits(int estimatedInputTokens, int outputTokenReserve, int maximumContextTokens) =>
        (long)estimatedInputTokens + outputTokenReserve <= maximumContextTokens;

    private static void ValidateArguments(
        IReadOnlyList<ChatMessage> messages,
        int currentTurnStartIndex,
        IReadOnlyList<DurableChatTurnRange> previousTurnRanges,
        int? maximumContextTokens,
        int outputTokenReserve)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(previousTurnRanges);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokenReserve);
        if (maximumContextTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumContextTokens));
        }
        if (currentTurnStartIndex < 0 || currentTurnStartIndex > messages.Count)
        {
            throw new InvalidOperationException("The current-turn boundary is outside the model input.");
        }

        var priorEnd = 0;
        foreach (var range in previousTurnRanges)
        {
            if (range.StartIndex < priorEnd
                || range.EndIndex < range.StartIndex
                || range.EndIndex > currentTurnStartIndex)
            {
                throw new InvalidOperationException("Prior-turn message ranges are invalid or overlap.");
            }
            priorEnd = range.EndIndex;
        }
    }

    private sealed class HeuristicDurableChatTokenEstimator
    {
        private readonly long[] cumulativeMessageByteCounts;
        private readonly int fixedContextByteCount;

        internal HeuristicDurableChatTokenEstimator(IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            this.cumulativeMessageByteCounts = new long[messages.Count + 1];
            for (var index = 0; index < messages.Count; index++)
            {
                this.cumulativeMessageByteCounts[index + 1] = this.cumulativeMessageByteCounts[index]
                    + JsonSerializer.SerializeToUtf8Bytes(
                        messages[index],
                        AIJsonUtilities.DefaultOptions).Length;
            }

            var byteCount = 0;
            if (!string.IsNullOrEmpty(options.Instructions))
            {
                byteCount += Encoding.UTF8.GetByteCount(options.Instructions);
            }

            if (options.ResponseFormat is not null)
            {
                byteCount += JsonSerializer.SerializeToUtf8Bytes(
                    options.ResponseFormat,
                    AIJsonUtilities.DefaultOptions).Length;
            }

            if (options.Tools is not null)
            {
                foreach (var tool in options.Tools)
                {
                    byteCount += Encoding.UTF8.GetByteCount(tool.Name);
                    byteCount += Encoding.UTF8.GetByteCount(tool.Description);
                    if (tool is AIFunction function)
                    {
                        byteCount += Encoding.UTF8.GetByteCount(function.JsonSchema.GetRawText());
                    }
                }
            }

            this.fixedContextByteCount = byteCount;
        }

        internal int EstimateFrom(int startIndex)
        {
            var messageCount = this.cumulativeMessageByteCounts.Length - startIndex - 1;
            var messageByteCount = this.cumulativeMessageByteCounts[^1]
                - this.cumulativeMessageByteCounts[startIndex];
            // A JSON array has two brackets, with one comma between each adjacent pair.
            var serializedMessagesByteCount = messageCount == 0
                ? 2
                : messageByteCount + messageCount + 1;
            var byteCount = checked((int)(serializedMessagesByteCount + this.fixedContextByteCount));

            // JSON byte length overestimates many tokenizers while remaining a deterministic,
            // provider-independent fallback. Applications can register a model-specific estimator.
            return checked((byteCount + 2) / 3 + (messageCount * 4));
        }
    }
}
