using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableChatTelemetryTests
{
    [Fact]
    public void ActivitySourceName_IsCorrect()
    {
        Assert.Equal("TemporalCommunity.Extensions.AI", DurableChatTelemetry.ActivitySourceName);
    }

    [Fact]
    public void SpanNames_AreCorrect()
    {
        // ChatSendSpanName is a Temporal-protocol span (workflow update dispatch), not an LLM call,
        // so it retains its original name rather than following the GenAI convention.
        Assert.Equal("durable_chat.send", DurableChatTelemetry.ChatSendSpanName);

        // Tool spans follow OTel GenAI semantic conventions.
        // The MEAI OpenTelemetryChatClient owns inference span names.
        Assert.Equal("execute_tool", DurableChatTelemetry.ExecuteToolOperationName);
    }

    [Fact]
    public void AttributeNames_AreCorrect()
    {
        Assert.Equal("gen_ai.operation.name", DurableChatTelemetry.OperationNameAttribute);
        Assert.Equal("conversation.id", DurableChatTelemetry.ConversationIdAttribute);
        Assert.Equal(
            "temporal.ai.chat.requested_model",
            DurableChatTelemetry.RequestedModelAttribute);
        Assert.Equal(
            "temporal.ai.turn.completion_reason",
            DurableChatTelemetry.TurnCompletionReasonAttribute);
        Assert.Equal("gen_ai.tool.name", DurableChatTelemetry.ToolNameAttribute);
        Assert.Equal("gen_ai.tool.call.id", DurableChatTelemetry.ToolCallIdAttribute);
        Assert.Equal("gen_ai.tool.type", DurableChatTelemetry.ToolTypeAttribute);
        Assert.Equal("gen_ai.tool.description", DurableChatTelemetry.ToolDescriptionAttribute);
        Assert.Equal("gen_ai.conversation.id", DurableChatTelemetry.GenAiConversationIdAttribute);
        Assert.Equal("error.type", DurableChatTelemetry.ErrorTypeAttribute);
    }

    [Fact]
    public void WorkflowMetricNames_AreStable()
    {
        Assert.Equal("temporal.ai.turn.outcomes", DurableChatTelemetry.TurnOutcomesMetricName);
        Assert.Equal(
            "temporal.ai.turn.admission.rejections",
            DurableChatTelemetry.TurnAdmissionRejectionsMetricName);
        Assert.Equal("temporal.ai.approval.outcomes", DurableChatTelemetry.ApprovalOutcomesMetricName);
        Assert.Equal(
            "temporal.ai.approval.resolutions",
            DurableChatTelemetry.ApprovalResolutionsMetricName);
        Assert.Equal("temporal.ai.approval.expiries", DurableChatTelemetry.ApprovalExpiriesMetricName);
        Assert.Equal(
            "temporal.ai.history.compaction.outcomes",
            DurableChatTelemetry.HistoryCompactionOutcomesMetricName);
    }

    [Fact]
    public void ActivitySource_IsCreatedWithCorrectName()
    {
        Assert.Equal(DurableChatTelemetry.ActivitySourceName, DurableChatTelemetry.ActivitySource.Name);
    }
}
