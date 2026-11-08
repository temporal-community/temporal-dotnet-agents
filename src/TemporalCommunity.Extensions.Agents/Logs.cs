using Microsoft.Extensions.Logging;

namespace TemporalCommunity.Extensions.Agents;

internal static partial class Logs
{
    // ── Activity logs ────────────────────────────────────────────────────────

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Agent activity started")]
    public static partial void LogAgentActivityStarted(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Agent activity completed " +
                  "(Input tokens: {InputTokenCount}, Output tokens: {OutputTokenCount}, Total tokens: {TotalTokenCount})")]
    public static partial void LogAgentActivityCompleted(
        this ILogger logger,
        string agentName,
        string workflowId,
        long? inputTokenCount,
        long? outputTokenCount,
        long? totalTokenCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Rebuilding conversation context from {HistoryEntryCount} history entries ({MessageCount} messages)")]
    public static partial void LogActivityHistoryRebuilt(
        this ILogger logger, string agentName, string workflowId, int historyEntryCount, int messageCount);

    // ── Workflow lifecycle logs ───────────────────────────────────────────────

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Agent workflow started (TTL: {TimeToLive})")]
    public static partial void LogWorkflowStarted(
        this ILogger logger, string agentName, string workflowId, TimeSpan timeToLive);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Workflow TTL elapsed, session complete")]
    public static partial void LogWorkflowTTLExpired(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Workflow history limit reached; triggering continue-as-new with {HistoryCount} history entries")]
    public static partial void LogWorkflowContinueAsNew(
        this ILogger logger, string agentName, string workflowId, int historyCount);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Workflow shutdown requested")]
    public static partial void LogWorkflowShutdownRequested(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Workflow update received (CorrelationId: {CorrelationId})")]
    public static partial void LogWorkflowUpdateReceived(
        this ILogger logger, string agentName, string workflowId, string correlationId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Workflow update completed (CorrelationId: {CorrelationId})")]
    public static partial void LogWorkflowUpdateCompleted(
        this ILogger logger, string agentName, string workflowId, string correlationId);

    // ── Client logs ───────────────────────────────────────────────────────────

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Sending update to agent workflow")]
    public static partial void LogClientSendingUpdate(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Agent workflow update completed successfully")]
    public static partial void LogClientUpdateCompleted(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Dispatching fire-and-forget update to agent workflow")]
    public static partial void LogClientFireAndForget(
        this ILogger logger, string agentName, string workflowId);

    // ── Proxy logs ────────────────────────────────────────────────────────────

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Agent session created")]
    public static partial void LogProxySessionCreated(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Dispatching request to agent workflow (FireAndForget: {IsFireAndForget})")]
    public static partial void LogProxyDispatchingRequest(
        this ILogger logger, string agentName, string workflowId, bool isFireAndForget);

    // ── In-workflow agent logs ────────────────────────────────────────────────

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug,
        Message = "[{AgentName}] Dispatching activity from orchestrating workflow (Turn: {TurnCount})")]
    public static partial void LogInWorkflowAgentDispatching(
        this ILogger logger, string agentName, int turnCount);

    // ── Routing logs (GAP 2) ──────────────────────────────────────────────────

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug,
        Message = "[{AgentName}/{WorkflowId}] Routing selected agent; dispatching")]
    public static partial void LogClientRouting(
        this ILogger logger, string agentName, string workflowId);

    // ── HITL logs (GAP 3) ─────────────────────────────────────────────────────

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Approval requested (RequestId: {RequestId}, Description: {Description})")]
    public static partial void LogWorkflowApprovalRequested(
        this ILogger logger, string agentName, string workflowId, string requestId, string description);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Approval resolved (RequestId: {RequestId}, Approved: {Approved})")]
    public static partial void LogWorkflowApprovalResolved(
        this ILogger logger, string agentName, string workflowId, string requestId, bool approved);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
        Message = "[{AgentName}/{WorkflowId}] Fire-and-forget turn failed; the turn was rolled back and its result is unavailable for session '{WorkflowId}'.")]
    public static partial void LogFireAndForgetActivityFailed(
        this ILogger logger, string agentName, string workflowId, Exception ex);

    // ── Durable agent per-tool invocation logs ─────────────────────

    [LoggerMessage(EventId = 26, Level = LogLevel.Information,
        Message = "[{AgentName}] Invoking tool '{ToolName}' as Temporal activity")]
    public static partial void LogAgentToolInvocationStarted(
        this ILogger logger, string agentName, string toolName);

    [LoggerMessage(EventId = 27, Level = LogLevel.Information,
        Message = "[{AgentName}] Tool '{ToolName}' completed")]
    public static partial void LogAgentToolInvocationCompleted(
        this ILogger logger, string agentName, string toolName);

    // ── Durable-agent workflow loop (Phase 3) ────────────────────────────

    [LoggerMessage(EventId = 29, Level = LogLevel.Information,
        Message = "[{AgentName}/{WorkflowId}] Durable agent turn started")]
    public static partial void LogDurableAgentTurnStarted(
        this ILogger logger, string agentName, string workflowId);

    [LoggerMessage(EventId = 30, Level = LogLevel.Debug,
        Message = "[{AgentName}] Durable agent iteration {Iteration} dispatched {ToolCallCount} tool call(s)")]
    public static partial void LogDurableAgentTurnIteration(
        this ILogger logger, string agentName, int iteration, int toolCallCount);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information,
        Message = "[{AgentName}] Durable agent turn completed in {TotalIterations} iteration(s)")]
    public static partial void LogDurableAgentTurnCompleted(
        this ILogger logger, string agentName, int totalIterations);

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning,
        Message = "[{AgentName}] Durable agent turn aborted after exceeding iteration cap ({IterationLimit}); returning structured error")]
    public static partial void LogDurableAgentTurnAborted(
        this ILogger logger, string agentName, int iterationLimit);

    [LoggerMessage(EventId = 33, Level = LogLevel.Warning,
        Message = "[{AgentName}/{WorkflowId}] Blocked model-returned tool call '{RequestedToolName}' " +
                  "for this run (CorrelationId: {CorrelationId}, Iteration: {Iteration})")]
    public static partial void LogRunToolCallBlocked(
        this ILogger logger,
        string agentName,
        string workflowId,
        string correlationId,
        int iteration,
        string? requestedToolName);
}
