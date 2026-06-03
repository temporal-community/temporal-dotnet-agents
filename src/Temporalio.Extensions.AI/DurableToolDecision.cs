namespace Temporalio.Extensions.AI;

/// <summary>
/// Discriminated union returned by <see cref="IDurableToolInterceptor{TContext}.BeforeToolCallAsync"/>
/// to control what happens when the dispatch loop is about to execute a tool activity.
/// </summary>
/// <remarks>
/// Use the static factory methods (<see cref="Proceed"/>, <see cref="PauseForApproval"/>,
/// <see cref="Skip"/>, <see cref="Block"/>) to construct instances.
/// This type is not wire-serialized. The internal DTO that crosses the Temporal
/// workflow/activity boundary is the serialized form; <c>DurableToolDecision</c> is the
/// developer-facing discriminated union only.
/// </remarks>
public abstract class DurableToolDecision
{
    // Sealed base — only the four inner subclasses may exist.
    private DurableToolDecision() { }

    /// <summary>
    /// Continue normal dispatch. The tool activity is invoked as-is.
    /// </summary>
    /// <param name="enrichedDescription">
    /// Optional human-readable description injected into the approval request description when
    /// the tool also has a <c>RequireApproval</c> flag set, or when a subsequent interceptor
    /// returns <see cref="PauseForApproval"/>.
    /// </param>
    /// <param name="modifiedArguments">
    /// Optional replacement argument dictionary. When set, the dispatch loop invokes the tool
    /// with these arguments instead of the LLM-supplied ones.
    /// Note: the LLM-supplied arguments are already in Temporal history from the LLM step
    /// activity; this substitution affects only the tool-dispatch event.
    /// </param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>A <see cref="ProceedDecision"/> instance.</returns>
    public static DurableToolDecision Proceed(
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
    /// Park the dispatch loop and wait for a human approval before the tool activity executes.
    /// <paramref name="description"/> feeds the approval request shown to the reviewer.
    /// </summary>
    /// <remarks>
    /// On execution paths that do not support workflow-parked HITL (e.g., scheduled jobs,
    /// sub-agent workflows), this outcome degrades to <see cref="Block"/> with a warning logged.
    /// </remarks>
    /// <param name="description">Human-readable approval request description.</param>
    /// <param name="metadata">Optional key/value metadata carried for audit purposes.</param>
    /// <returns>An <see cref="ApprovalRequiredDecision"/> instance.</returns>
    public static DurableToolDecision PauseForApproval(
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
    public static DurableToolDecision Skip(
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
    public static DurableToolDecision Block(
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
    public sealed class ProceedDecision : DurableToolDecision
    {
        /// <inheritdoc cref="DurableToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public string? EnrichedDescription { get; init; }

        /// <inheritdoc cref="DurableToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, object?>? ModifiedArguments { get; init; }

        /// <inheritdoc cref="DurableToolDecision.Proceed(string?, IReadOnlyDictionary{string, object?}?, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: park the dispatch loop for human approval.
    /// </summary>
    public sealed class ApprovalRequiredDecision : DurableToolDecision
    {
        /// <inheritdoc cref="DurableToolDecision.PauseForApproval(string, IReadOnlyDictionary{string, string}?)"/>
        public required string Description { get; init; }

        /// <inheritdoc cref="DurableToolDecision.PauseForApproval(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: inject a synthetic tool result without executing the tool activity.
    /// </summary>
    public sealed class SkipDecision : DurableToolDecision
    {
        /// <inheritdoc cref="DurableToolDecision.Skip(string, IReadOnlyDictionary{string, string}?)"/>
        public required string SyntheticResult { get; init; }

        /// <inheritdoc cref="DurableToolDecision.Skip(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Outcome: block tool dispatch and inject an error result.
    /// </summary>
    public sealed class BlockDecision : DurableToolDecision
    {
        /// <inheritdoc cref="DurableToolDecision.Block(string, IReadOnlyDictionary{string, string}?)"/>
        public required string Reason { get; init; }

        /// <inheritdoc cref="DurableToolDecision.Block(string, IReadOnlyDictionary{string, string}?)"/>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }
}
