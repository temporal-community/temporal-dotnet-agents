namespace Temporalio.Extensions.Agents;

/// <summary>
/// Discriminated union returned by <see cref="IAgentToolInterceptor.BeforeToolCallAsync"/>
/// to control what happens when the turn loop is about to dispatch a tool activity.
/// </summary>
/// <remarks>
/// Use the static factory methods (<see cref="Proceed"/>, <see cref="PauseForApproval"/>,
/// <see cref="Skip"/>, <see cref="Block"/>) to construct instances.
/// </remarks>
public abstract class AgentToolDecision
{
    // Sealed base — only the four inner subclasses may exist.
    private AgentToolDecision() { }

    /// <summary>
    /// Continue normal dispatch. The tool activity is invoked as-is.
    /// </summary>
    /// <param name="enrichedDescription">
    /// Optional human-readable description injected into the
    /// <see cref="AI.DurableApprovalRequest.Description"/> when the tool also has
    /// <see cref="DurableToolOptions.RequireApproval"/> set, or when a subsequent
    /// interceptor returns <see cref="PauseForApproval"/>.
    /// </param>
    /// <param name="modifiedArguments">
    /// Optional replacement argument dictionary. When set, the turn loop dispatches
    /// <c>InvokeAgentTool</c> with these arguments instead of the LLM-supplied ones.
    /// Note: the LLM-supplied arguments are already in Temporal history from
    /// <c>RunDurableAgentStep</c>; this substitution affects only the
    /// <c>InvokeAgentToolInput</c> event, not earlier history events.
    /// </param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>A <see cref="ProceedDecision"/> instance.</returns>
    public static AgentToolDecision Proceed(
        string? enrichedDescription = null,
        IReadOnlyDictionary<string, object?>? modifiedArguments = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new ProceedDecision
        {
            EnrichedDescription = enrichedDescription,
            ModifiedArguments = modifiedArguments,
            Metadata = metadata,
        };

    /// <summary>
    /// Park the turn loop and wait for a human approval before dispatching the tool activity.
    /// Composes with Feature A (workflow-parked HITL): <paramref name="description"/> feeds
    /// <see cref="AI.DurableApprovalRequest.Description"/>.
    /// </summary>
    /// <remarks>
    /// On <c>AgentJobWorkflow</c> and <c>TemporalAIAgent</c> (neither has a
    /// <c>DurableApprovalMixin</c>), this outcome degrades to <see cref="Block"/> with a
    /// warning logged explaining the degradation.
    /// </remarks>
    /// <param name="description">Human-readable approval request description.</param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>An <see cref="ApprovalRequiredDecision"/> instance.</returns>
    public static AgentToolDecision PauseForApproval(
        string description,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        return new ApprovalRequiredDecision
        {
            Description = description,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Short-circuit dispatch. The tool activity is NOT invoked; instead
    /// <paramref name="syntheticResult"/> is injected as a
    /// <see cref="Microsoft.Extensions.AI.FunctionResultContent"/> so the LLM receives a
    /// well-formed tool result without the activity executing.
    /// </summary>
    /// <param name="syntheticResult">The fake result text to inject.</param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>A <see cref="SkipDecision"/> instance.</returns>
    public static AgentToolDecision Skip(
        string syntheticResult,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(syntheticResult);
        return new SkipDecision
        {
            SyntheticResult = syntheticResult,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Block dispatch. The tool activity is NOT invoked; an error
    /// <see cref="Microsoft.Extensions.AI.FunctionResultContent"/> carrying
    /// <paramref name="reason"/> is injected so the LLM is informed the call was blocked.
    /// </summary>
    /// <param name="reason">Human-readable block reason.</param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>A <see cref="BlockDecision"/> instance.</returns>
    public static AgentToolDecision Block(
        string reason,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new BlockDecision
        {
            Reason = reason,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Outcome: proceed with tool dispatch (optionally enriched/modified).
    /// </summary>
    public sealed class ProceedDecision : AgentToolDecision
    {
        /// <inheritdoc cref="AgentToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public string? EnrichedDescription { get; init; }

        /// <inheritdoc cref="AgentToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, object?>? ModifiedArguments { get; init; }

        /// <inheritdoc cref="AgentToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: park the turn loop for human approval.
    /// </summary>
    public sealed class ApprovalRequiredDecision : AgentToolDecision
    {
        /// <inheritdoc cref="AgentToolDecision.PauseForApproval(string, IReadOnlyDictionary{string, string}?)"/>
        public required string Description { get; init; }

        /// <inheritdoc cref="AgentToolDecision.PauseForApproval(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: inject a synthetic tool result without executing the tool.
    /// </summary>
    public sealed class SkipDecision : AgentToolDecision
    {
        /// <inheritdoc cref="AgentToolDecision.Skip(string, IReadOnlyDictionary{string, string}?)"/>
        public required string SyntheticResult { get; init; }

        /// <inheritdoc cref="AgentToolDecision.Skip(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: block tool dispatch and inject an error result.
    /// </summary>
    public sealed class BlockDecision : AgentToolDecision
    {
        /// <inheritdoc cref="AgentToolDecision.Block(string, IReadOnlyDictionary{string, string}?)"/>
        public required string Reason { get; init; }

        /// <inheritdoc cref="AgentToolDecision.Block(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }
}
