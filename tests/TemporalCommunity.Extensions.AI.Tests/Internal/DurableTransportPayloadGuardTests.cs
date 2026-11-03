using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Internal;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests.Internal;

public class DurableTransportPayloadGuardTests
{
    [Fact]
    public void EnsureWithinLimit_UsesConfiguredPayloadConverterAndCodecAllowance()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var value = new string('x', 128);
        var serializedSize = converter.ToPayload(value).CalculateSize();

        DurableTransportPayloadGuard.EnsureWithinLimit(
            converter,
            value,
            maximumPayloadSizeBytes: serializedSize + 16,
            payloadCodecExpansionAllowanceBytes: 16,
            boundary: "test payload");

        var exception = Assert.Throws<DurableTransportPayloadTooLargeException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                value,
                maximumPayloadSizeBytes: serializedSize + 15,
                payloadCodecExpansionAllowanceBytes: 16,
                boundary: "test payload"));

        Assert.Equal("test payload", exception.Boundary);
        Assert.Equal(serializedSize, exception.SerializedSizeBytes);
        Assert.Equal(serializedSize + 15, exception.MaximumPayloadSizeBytes);
        Assert.Equal(16, exception.PayloadCodecExpansionAllowanceBytes);
    }

    [Fact]
    public void EnsureWithinLimit_RejectsInvalidBudgets()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        Assert.Throws<InvalidOperationException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                "value",
                maximumPayloadSizeBytes: 0,
                payloadCodecExpansionAllowanceBytes: 0,
                boundary: "test payload"));
        Assert.Throws<InvalidOperationException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                "value",
                maximumPayloadSizeBytes: 10,
                payloadCodecExpansionAllowanceBytes: 10,
                boundary: "test payload"));
    }

    [Fact]
    public void EnsureWithinLimit_GuardsCompleteHistorySummarizationInput()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var input = new DurableHistorySummarizationInput
        {
            Turns =
            [
                new DurableHistorySummaryTurn
                {
                    TurnId = "turn-1",
                    Messages = [new ChatMessage(ChatRole.User, new string('x', 256))],
                },
                new DurableHistorySummaryTurn
                {
                    TurnId = "turn-2",
                    Messages = [new ChatMessage(ChatRole.Assistant, "response")],
                },
            ],
        };
        var serializedSize = converter.ToPayload(input).CalculateSize();

        var exception = Assert.Throws<DurableTransportPayloadTooLargeException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                input,
                maximumPayloadSizeBytes: serializedSize - 1,
                payloadCodecExpansionAllowanceBytes: 0,
                boundary: "history summarizer activity input"));

        Assert.Equal("history summarizer activity input", exception.Boundary);
        Assert.Equal(serializedSize, exception.SerializedSizeBytes);
    }

    [Fact]
    public void EnsureWithinLimit_GuardsCompactionMarkerAndCompleteContinueAsNewInput()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
#pragma warning disable TA002 // CompactionMarkerEntry is an experimental durable wire type.
        var marker = new CompactionMarkerEntry
        {
            CorrelationId = "summary-1",
            EntryId = "summary-1",
            TurnId = "summary-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
            FirstReplacedEntryId = "entry-1",
            LastReplacedEntryId = "entry-8",
            ReplacedEntryCount = 8,
            FirstReplacedTurnId = "turn-1",
            LastReplacedTurnId = "turn-4",
            ReplacedTurnCount = 4,
            Strategy = "summarize-oldest-turns",
            ModelId = "test",
            Summary = new DurableSessionSummary { Text = "bounded summary" },
        };
#pragma warning restore TA002
        var history = new List<DurableSessionEntry> { marker };
        var historySize = converter.ToPayload(history).CalculateSize();

        var historyException = Assert.Throws<DurableTransportPayloadTooLargeException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                history,
                maximumPayloadSizeBytes: historySize - 1,
                payloadCodecExpansionAllowanceBytes: 0,
                boundary: "compacted retained history"));

        Assert.Equal(historySize, historyException.SerializedSizeBytes);

        var continueAsNewInput = DurableChatWorkflowBase<ChatResponse>.CreateContinueAsNewInput(
            new DurableChatWorkflowInput(),
            history,
            [],
            DateTimeOffset.UnixEpoch,
            [],
            completedTurnCount: 4);
        var inputSize = converter.ToPayload(continueAsNewInput).CalculateSize();
        var inputException = Assert.Throws<DurableTransportPayloadTooLargeException>(() =>
            DurableTransportPayloadGuard.EnsureWithinLimit(
                converter,
                continueAsNewInput,
                maximumPayloadSizeBytes: inputSize - 1,
                payloadCodecExpansionAllowanceBytes: 0,
                boundary: "continue-as-new workflow input"));

        Assert.Equal(inputSize, inputException.SerializedSizeBytes);
    }
}
