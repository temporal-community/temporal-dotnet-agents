using Microsoft.Extensions.Logging;

namespace TemporalCommunity.Extensions.AI;

/// <summary>
/// Centralized <see cref="LoggerMessage"/> source-gen delegates for the
/// <c>TemporalCommunity.Extensions.AI</c> library. Mirrors the pattern used in the
/// <c>TemporalCommunity.Extensions.Agents</c> library (<c>Agents/Logs.cs</c>).
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
}
