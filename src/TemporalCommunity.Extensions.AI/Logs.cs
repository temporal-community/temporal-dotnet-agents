using Microsoft.Extensions.Logging;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Centralized <see cref="LoggerMessage"/> source-gen delegates for the
/// <c>TemporalCommunity.Extensions.AI</c> library.
/// All high-frequency log paths must use these delegates to avoid per-call allocations.
/// </summary>
internal static partial class Logs
{
    // ── Chat activity logs ───────────────────────────────────────────────────

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Executing durable chat activity for conversation {ConversationId}, turn {TurnNumber}")]
    public static partial void LogChatActivityStarted(
        this ILogger logger, string? conversationId, int turnNumber);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Durable chat activity completed for conversation {ConversationId}, turn {TurnNumber}")]
    public static partial void LogChatActivityCompleted(
        this ILogger logger, string? conversationId, int turnNumber);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Durable chat activity failed for conversation {ConversationId}, turn {TurnNumber}")]
    public static partial void LogChatActivityFailed(
        this ILogger logger, Exception ex, string? conversationId, int turnNumber);

    // ── Chat step activity logs ──────────────────────────────────────────────

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Executing durable chat step activity for conversation {ConversationId}, turn {TurnNumber}")]
    public static partial void LogChatStepStarted(
        this ILogger logger, string? conversationId, int turnNumber);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Durable chat step activity completed for conversation {ConversationId}, turn {TurnNumber} (IsFinal={IsFinal}, ToolCalls={ToolCallCount})")]
    public static partial void LogChatStepCompleted(
        this ILogger logger, string? conversationId, int turnNumber, bool isFinal, int toolCallCount);

    // ── Function activity logs ───────────────────────────────────────────────

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Invoking durable function {FunctionName}")]
    public static partial void LogFunctionInvoking(
        this ILogger logger, string functionName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "Durable function {FunctionName} completed")]
    public static partial void LogFunctionCompleted(
        this ILogger logger, string functionName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error,
        Message = "Durable function {FunctionName} failed")]
    public static partial void LogFunctionFailed(
        this ILogger logger, Exception ex, string functionName);

    // ── Tool interceptor activity logs ───────────────────────────────────────

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "RunToolInterceptor dispatched for tool '{ToolName}' but no IDurableToolInterceptor<DurableToolContext> is registered in DI. Defaulting to Proceed.")]
    public static partial void LogToolInterceptorNotRegistered(
        this ILogger logger, string toolName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "IDurableToolInterceptor.BeforeToolCallAsync threw for tool '{ToolName}'. Defaulting to Block.")]
    public static partial void LogToolInterceptorThrew(
        this ILogger logger, Exception ex, string toolName);

    // ── Session client logs ──────────────────────────────────────────────────

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug,
        Message = "Sending chat to session {WorkflowId}")]
    public static partial void LogClientSendingChat(
        this ILogger logger, string workflowId);

    // ── Data converter plugin logs ───────────────────────────────────────────

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "DurableAIDataConverter applied to TemporalClient (DataConverter was default).")]
    public static partial void LogConverterAppliedToClient(this ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug,
        Message = "DataConverter already set to {Type}; DurableAIDataConverter not applied.")]
    public static partial void LogConverterSkippedForClient(this ILogger logger, string type);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
        Message = "DurableAIDataConverter applied to TemporalClientConnectOptions (DataConverter was default).")]
    public static partial void LogConverterAppliedToConnectOptions(this ILogger logger);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "DataConverter already set to {Type}; DurableAIDataConverter not applied.")]
    public static partial void LogConverterSkippedForConnectOptions(this ILogger logger, string type);

    // ── Embedding activity logs ──────────────────────────────────────────────

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug,
        Message = "Executing durable embedding activity for {Count} inputs")]
    public static partial void LogEmbeddingActivityStarted(
        this ILogger logger, int count);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug,
        Message = "Durable embedding activity completed")]
    public static partial void LogEmbeddingActivityCompleted(this ILogger logger);

    [LoggerMessage(EventId = 18, Level = LogLevel.Error,
        Message = "Durable embedding activity failed")]
    public static partial void LogEmbeddingActivityFailed(this ILogger logger, Exception ex);

    // ── Per-call activity tags ──────────────────────────────────────────────

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning,
        Message = "WithChatClientTag was used but Activity.Current is null; tags ({TagKeys}) will not be applied. Ensure the OpenTelemetry pipeline is configured.")]
    public static partial void LogChatClientTagsSkipped(this ILogger logger, string tagKeys);

    // ── Durable toolset resolution and validation ──────────────────────────

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
        Message = "Resolving durable toolsets")]
    public static partial void LogToolsetResolverStarted(this ILogger logger);

    [LoggerMessage(EventId = 21, Level = LogLevel.Debug,
        Message = "Resolved durable toolsets (Toolsets={ToolsetCount}, Functions={FunctionCount})")]
    public static partial void LogToolsetResolverCompleted(
        this ILogger logger, int toolsetCount, int functionCount);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
        Message = "Durable toolset resolution failed ({Reason})")]
    public static partial void LogToolsetResolverFailed(
        this ILogger logger, Exception exception, string reason);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "Durable toolset activity validation rejected input ({Reason})")]
    public static partial void LogToolsetValidationRejected(this ILogger logger, string reason);

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning,
        Message = "Durable turn toolset narrowing rejected ({Reason})")]
    public static partial void LogToolsetNarrowingRejected(this ILogger logger, string reason);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
        Message = "Model requested a function outside the active durable declaration set for workflow {WorkflowId}, turn {TurnNumber}, model iteration {ModelIteration}, call index {CallIndex}; returning the safe blocked result without interceptor, approval, or tool dispatch")]
    public static partial void LogDurableToolCallNotEnabled(
        this ILogger logger,
        string workflowId,
        int turnNumber,
        int modelIteration,
        int callIndex);
}
