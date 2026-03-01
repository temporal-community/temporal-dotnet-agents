using System.Diagnostics;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// OpenTelemetry instrumentation constants for TemporalAgents.
/// <para>
/// Use an OTel SDK (e.g. <c>OpenTelemetry.Extensions.Hosting</c>) with
/// <c>.AddSource(TemporalAgentTelemetry.ActivitySourceName)</c> to receive these spans.
/// </para>
/// </summary>
public static class TemporalAgentTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> used by this library.</summary>
    public const string ActivitySourceName = "Temporalio.Extensions.Agents";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // ── Span names ─────────────────────────────────────────────────────────────

    /// <summary>Span emitted by <c>AgentActivities.ExecuteAgentAsync</c> for each agent turn.</summary>
    public const string AgentTurnSpanName = "agent.turn";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.RunAgentAsync</c> when sending an update.</summary>
    public const string AgentClientSendSpanName = "agent.client.send";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.ScheduleAgentAsync</c> when creating a recurring schedule.</summary>
    public const string AgentScheduleCreateSpanName = "agent.schedule.create";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.RunAgentDelayedAsync</c> when starting a delayed workflow.</summary>
    public const string AgentScheduleDelayedSpanName = "agent.schedule.delayed";

    /// <summary>Span emitted by <c>ScheduleActivities.ScheduleOneTimeAgentRunAsync</c> when scheduling a one-time run.</summary>
    public const string AgentScheduleOneTimeSpanName = "agent.schedule.one_time";

    // ── Attribute names ────────────────────────────────────────────────────────

    /// <summary>The registered name of the agent being invoked.</summary>
    public const string AgentNameAttribute = "agent.name";

    /// <summary>The Temporal workflow ID that backs the agent session.</summary>
    public const string AgentSessionIdAttribute = "agent.session_id";

    /// <summary>The correlation ID linking the request to its response.</summary>
    public const string AgentCorrelationIdAttribute = "agent.correlation_id";

    /// <summary>Number of input (prompt) tokens consumed by the LLM.</summary>
    public const string InputTokensAttribute = "agent.input_tokens";

    /// <summary>Number of output (completion) tokens produced by the LLM.</summary>
    public const string OutputTokensAttribute = "agent.output_tokens";

    /// <summary>Total tokens (input + output).</summary>
    public const string TotalTokensAttribute = "agent.total_tokens";

    /// <summary>The ID of the recurring schedule being created.</summary>
    public const string ScheduleIdAttribute = "schedule.id";

    /// <summary>The delay before a deferred run starts, as <see cref="TimeSpan.ToString()"/>.</summary>
    public const string ScheduleDelayAttribute = "schedule.delay";

    /// <summary>The run ID of a one-time scheduled job.</summary>
    public const string ScheduleJobIdAttribute = "schedule.job_id";
}
