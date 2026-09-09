namespace TemporalCommunity.Extensions.Agents.Session;

/// <summary>
/// Why a tool activity could not establish a <see cref="TemporalAgentContext"/>.
/// </summary>
/// <remarks>
/// Recorded by the tool activity at the point it decides to skip context setup, so
/// <see cref="TemporalAgentContext.Current"/> can name the execution path instead of reporting a
/// bare absence. Deliberately a typed signal rather than something inferred from a caught
/// exception: a tool body throws <see cref="System.InvalidOperationException"/> for its own
/// reasons, and relabelling one of those as an unsupported-path diagnostic would mislead.
/// </remarks>
internal enum ContextUnavailableReason
{
    /// <summary>
    /// The activity's workflow ID is not an agent session ID — the tool was invoked by a
    /// workflow-local sub-agent, so the activity runs under the orchestrating workflow.
    /// </summary>
    SubAgentPath,

    /// <summary>
    /// The workflow ID parsed but names a different agent identity, as a scheduled job's
    /// <c>ta-{agent}-scheduled-{runId}</c> does. Attaching a session would target the wrong
    /// workflow.
    /// </summary>
    ScheduledJobPath,
}
