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
        };
        // END SNIPPET docs/how-to/MEAI/usage.md#library-owned-history-summarizer
        _ = summaryOptions;
    }
}
