using Microsoft.Extensions.AI;

namespace TemporalCommunity.Extensions.AI;

/// <summary>Classifies one provider response for the durable managed tool loop.</summary>
internal static class DurableChatCompletionPolicy
{
    internal static DurableChatStepClassification Classify(
        ChatFinishReason? finishReason,
        int toolCallCount)
    {
        var hasToolCalls = toolCallCount > 0;

        if (finishReason == ChatFinishReason.Length ||
            finishReason == ChatFinishReason.ContentFilter)
        {
            return new(
                DurableChatStepDisposition.IncompleteResponse,
                IsProviderOutputContradictory: hasToolCalls);
        }

        if (finishReason == ChatFinishReason.ToolCalls)
        {
            return hasToolCalls
                ? new(DurableChatStepDisposition.ContinueWithTools, false)
                : new(DurableChatStepDisposition.IncompleteResponse, true);
        }

        if (finishReason == ChatFinishReason.Stop)
        {
            return hasToolCalls
                ? new(DurableChatStepDisposition.IncompleteResponse, true)
                : new(DurableChatStepDisposition.FinalResponse, false);
        }

        if (finishReason is null)
        {
            // Preserve the pre-finish-reason contract for providers, middleware, and recorded
            // activity results that do not supply the field.
            return hasToolCalls
                ? new(DurableChatStepDisposition.ContinueWithTools, false)
                : new(DurableChatStepDisposition.FinalResponse, false);
        }

        // Unknown non-null values are not authorized to dispatch durable tools. This is a
        // fail-safe boundary for provider and middleware extensions the library does not know.
        return new(DurableChatStepDisposition.IncompleteResponse, true);
    }
}

internal enum DurableChatStepDisposition
{
    FinalResponse,
    ContinueWithTools,
    IncompleteResponse,
}

internal readonly record struct DurableChatStepClassification(
    DurableChatStepDisposition Disposition,
    bool IsProviderOutputContradictory);
