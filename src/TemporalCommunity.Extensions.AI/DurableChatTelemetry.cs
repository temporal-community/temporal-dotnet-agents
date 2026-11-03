using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// OpenTelemetry instrumentation constants for durable AI operations.
/// <para>
/// Register with:
/// <c>.AddSource(DurableChatTelemetry.ActivitySourceName)</c>
/// </para>
/// </summary>
/// <remarks>
/// Tool execution span names follow the OpenTelemetry GenAI semantic conventions at the
/// Development stability tier
/// (https://opentelemetry.io/docs/specs/semconv/gen-ai/).
/// Names and attributes may change before the specification reaches Stable.
/// </remarks>
public static class DurableChatTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> used by this library.</summary>
    public const string ActivitySourceName = "TemporalCommunity.Extensions.AI";

    /// <summary>The name of the <see cref="Meter"/> used by this library.</summary>
    public const string MeterName = "TemporalCommunity.Extensions.AI";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> ToolsetResolverAttempts = Meter.CreateCounter<long>(
        "temporal.ai.toolset.resolver.attempts");
    internal static readonly Histogram<int> ToolsetResolverSelectedToolsets =
        Meter.CreateHistogram<int>("temporal.ai.toolset.resolver.selected_toolsets");
    internal static readonly Histogram<int> ToolsetResolverSelectedFunctions =
        Meter.CreateHistogram<int>("temporal.ai.toolset.resolver.selected_functions");
    internal static readonly Histogram<long> ToolsetDeclarationSnapshotBytes =
        Meter.CreateHistogram<long>(
            "temporal.ai.toolset.declaration_snapshot.size",
            unit: "By",
            description: "Serialized bytes in the once-per-session durable toolset manifest.");
    internal static readonly Counter<long> ToolsetValidationRejections = Meter.CreateCounter<long>(
        "temporal.ai.toolset.validation.rejections");
    /// <summary>Counter for completed durable turn outcomes.</summary>
    public const string TurnOutcomesMetricName = "temporal.ai.turn.outcomes";

    /// <summary>Counter for durable turn-admission rejections.</summary>
    public const string TurnAdmissionRejectionsMetricName = "temporal.ai.turn.admission.rejections";

    /// <summary>Counter for completed durable approval outcomes.</summary>
    public const string ApprovalOutcomesMetricName = "temporal.ai.approval.outcomes";

    /// <summary>Counter for approval-resolution update outcomes.</summary>
    public const string ApprovalResolutionsMetricName = "temporal.ai.approval.resolutions";

    /// <summary>Counter for deterministic approval expirations.</summary>
    public const string ApprovalExpiriesMetricName = "temporal.ai.approval.expiries";

    /// <summary>Counter for retained-history compaction outcomes.</summary>
    public const string HistoryCompactionOutcomesMetricName = "temporal.ai.history.compaction.outcomes";
    // ── Span names ───────────────────────────────────────────────────────────

    /// <summary>
    /// Span emitted by <see cref="DurableChatSessionClient"/> when sending a chat request.
    /// This is a Temporal-protocol-level span (workflow update dispatch), not an outbound
    /// LLM call, so it intentionally does not follow the GenAI inference span naming convention.
    /// </summary>
    public const string ChatSendSpanName = "durable_chat.send";

    /// <summary>
    /// GenAI operation name prefix for tool execution spans.
    /// The full span name is <c>"execute_tool {toolName}"</c>, constructed dynamically at
    /// call sites. Follows the OTel GenAI semantic convention <c>"execute_tool {tool_name}"</c>
    /// format.
    /// </summary>
    public const string ExecuteToolOperationName = "execute_tool";

    /// <summary>Activity-side span emitted while resolving a worker-owned toolset manifest.</summary>
    public const string ToolsetResolveSpanName = "durable_toolset.resolve";

    // ── Attribute names ──────────────────────────────────────────────────────

    /// <summary>The GenAI operation name (e.g., <c>"chat"</c> or <c>"execute_tool"</c>).</summary>
    public const string OperationNameAttribute = "gen_ai.operation.name";

    /// <summary>The conversation/session identifier.</summary>
    public const string ConversationIdAttribute = "conversation.id";

    /// <summary>The model requested for a Temporal update, distinct from inference attributes.</summary>
    public const string RequestedModelAttribute = "temporal.ai.chat.requested_model";

    /// <summary>The package-level reason the durable managed turn completed.</summary>
    public const string TurnCompletionReasonAttribute = "temporal.ai.turn.completion_reason";

    /// <summary>The name of the tool being invoked.</summary>
    public const string ToolNameAttribute = "gen_ai.tool.name";

    /// <summary>The call ID of the tool invocation.</summary>
    public const string ToolCallIdAttribute = "gen_ai.tool.call.id";

    /// <summary>The type of tool being invoked.</summary>
    public const string ToolTypeAttribute = "gen_ai.tool.type";

    /// <summary>The registered description of the tool being invoked.</summary>
    public const string ToolDescriptionAttribute = "gen_ai.tool.description";

    /// <summary>Tool-call arguments, included only when telemetry content capture is enabled.</summary>
    public const string ToolCallArgumentsAttribute = "gen_ai.tool.call.arguments";

    /// <summary>Tool-call result, included only when telemetry content capture is enabled.</summary>
    public const string ToolCallResultAttribute = "gen_ai.tool.call.result";

    /// <summary>The GenAI conversation identifier for the durable session.</summary>
    public const string GenAiConversationIdAttribute = "gen_ai.conversation.id";

    /// <summary>The bounded exception type associated with a failed tool invocation.</summary>
    public const string ErrorTypeAttribute = "error.type";
}
