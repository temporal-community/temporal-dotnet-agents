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

        var estimate = Estimate(messages, options, estimator);
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
                || range.IsProtected)
            {
                break;
            }

            dropThrough = range.EndIndex;
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
            ?? HeuristicDurableChatTokenEstimator.EstimateInputTokens(messages, options);
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
        internal static int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            var byteCount = JsonSerializer.SerializeToUtf8Bytes(
                messages,
                AIJsonUtilities.DefaultOptions).Length;

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

            // JSON byte length overestimates many tokenizers while remaining a deterministic,
            // provider-independent fallback. Applications can register a model-specific estimator.
            return checked((byteCount + 2) / 3 + (messages.Count * 4));
        }
    }
}
