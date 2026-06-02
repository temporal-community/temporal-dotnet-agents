namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Concrete DTO returned from the <c>RunToolInterceptor</c> activity.
/// Bridges between the public <see cref="AgentToolDecision"/> API and the activity I/O
/// boundary, which must be source-gen serializable.
/// </summary>
internal sealed class AgentToolInterceptorResult
{
    /// <summary>The outcome decided by the interceptor.</summary>
    public AgentToolOutcome Outcome { get; init; }

    /// <summary>
    /// Human-readable description used when the outcome is <see cref="AgentToolOutcome.PauseForApproval"/>
    /// (the approval request description) or when <see cref="AgentToolOutcome.Proceed"/> and
    /// the interceptor supplied an enriched description for a <c>RequireApproval</c>-flagged tool.
    /// </summary>
    public string? EnrichedDescription { get; init; }

    /// <summary>
    /// Replacement argument dictionary when <see cref="Outcome"/> is
    /// <see cref="AgentToolOutcome.Proceed"/> and the interceptor requested argument substitution.
    /// </summary>
    public Dictionary<string, object?>? ModifiedArguments { get; init; }

    /// <summary>Key/value metadata carried for audit purposes. Present on all outcomes.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Semantic depends on <see cref="Outcome"/>:
    /// <list type="bullet">
    /// <item><see cref="AgentToolOutcome.Skip"/> — the synthetic result text injected as <c>FunctionResultContent</c>.</item>
    /// <item><see cref="AgentToolOutcome.Block"/> — the block reason injected as an error <c>FunctionResultContent</c>.</item>
    /// <item><see cref="AgentToolOutcome.PauseForApproval"/> — the human-readable description (same as <see cref="EnrichedDescription"/>).</item>
    /// <item>All other outcomes — <see langword="null"/>.</item>
    /// </list>
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Outcome of a <c>RunToolInterceptor</c> activity, transported across the workflow/activity
/// boundary as the <see cref="AgentToolInterceptorResult.Outcome"/> field.
/// </summary>
internal enum AgentToolOutcome
{
    /// <summary>Proceed with normal tool dispatch.</summary>
    Proceed,

    /// <summary>Park the turn loop and wait for a human approval before dispatching.</summary>
    PauseForApproval,

    /// <summary>Skip dispatch; inject a synthetic result.</summary>
    Skip,

    /// <summary>Block dispatch; inject an error result.</summary>
    Block,
}
