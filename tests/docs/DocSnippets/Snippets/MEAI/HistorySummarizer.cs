using TemporalCommunity.Extensions.AI;

namespace DocSnippets.MEAI;

internal static class HistorySummarizer
{
    internal static void Configure()
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#library-owned-history-summarizer
        var summaryOptions = new DurableExecutionOptions
        {
            TaskQueue = "durable-chat",
            DefaultChatClientKey = "primary",
            DefaultHistorySummarizerClientKey = "summarizer",
            HistorySummarizerMaxContextTokens = 8192,
            HistorySummarizerOutputTokenReserve = 1024,
            DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.SummarizeOldestTurns,
        };
        // END SNIPPET docs/how-to/MEAI/usage.md#library-owned-history-summarizer
        _ = summaryOptions;

        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#retained-history-compaction-and-recovery
        var trimOptions = new DurableExecutionOptions
        {
            TaskQueue = "durable-chat",
            DefaultHistoryCompactionStrategy = DurableHistoryCompactionStrategy.TrimRecentTurns,
        };
        // END SNIPPET docs/how-to/MEAI/usage.md#retained-history-compaction-and-recovery
        _ = trimOptions;
    }

    internal static async Task RecoverAsync(
        IDurableChatSessionClient sessionClient,
        string conversationId)
    {
        // BEGIN SNIPPET docs/how-to/MEAI/usage.md#retained-history-compaction-and-recovery
        var status = await sessionClient.GetHistoryBudgetAsync(conversationId);
        Console.WriteLine(
            $"History: {status.HistoryEventCount}/{status.HistoryEventSoftLimit} events " +
            $"({status.HistoryEventReserveLimit} reserve); " +
            $"{status.HistorySizeBytes}/{status.HistorySizeSoftLimitBytes} bytes " +
            $"({status.HistorySizeReserveLimitBytes} reserve).");
        Console.WriteLine(
            $"Admission restricted: {status.IsAdmissionRestricted}; reserve reached: {status.IsReserveReached}; " +
            $"joiners: {status.ConcurrentTurnJoiners}/{status.MaxConcurrentTurnJoiners}.");
        if (status.IsHistoryBlocked)
        {
            var recovery = await sessionClient.RetryHistoryCompactionAsync(
                conversationId,
                operationId: Guid.NewGuid().ToString("N"));
            if (recovery.IsBlocked)
            {
                await sessionClient.TrimBlockedHistoryAsync(
                    conversationId,
                    operationId: Guid.NewGuid().ToString("N"));
            }
        }
        // END SNIPPET docs/how-to/MEAI/usage.md#retained-history-compaction-and-recovery
    }
}
