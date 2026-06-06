namespace Temporalio.Extensions.AI.Tools;

/// <summary>
/// Concrete DTO returned from the <c>RunToolInterceptor</c> activity on the MEAI path.
/// Bridges between the public <see cref="DurableToolDecision"/> API and the activity I/O
/// boundary, which must be source-gen serializable.
/// </summary>
internal sealed class DurableToolInterceptorResult
{
    /// <summary>
    /// Maps a <see cref="DurableToolDecision"/> discriminated union to a serializable
    /// <see cref="DurableToolInterceptorResult"/> DTO. This factory is the single source of
    /// truth for the decision-to-DTO mapping; both the MEAI and MAF activity paths delegate here.
    /// </summary>
    /// <param name="decision">The interceptor decision to convert.</param>
    /// <returns>A populated <see cref="DurableToolInterceptorResult"/>.</returns>
    internal static DurableToolInterceptorResult FromDecision(DurableToolDecision decision) =>
        decision switch
        {
            DurableToolDecision.ProceedDecision p => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Proceed,
                EnrichedDescription = p.EnrichedDescription,
                ModifiedArguments = p.ModifiedArguments is null
                    ? null
                    : new Dictionary<string, object?>(p.ModifiedArguments),
                Metadata = p.Metadata is null
                    ? null
                    : new Dictionary<string, string>(p.Metadata),
            },
            DurableToolDecision.ApprovalRequiredDecision a => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.PauseForApproval,
                EnrichedDescription = a.Description,
                Message = a.Description,
                Metadata = a.Metadata is null
                    ? null
                    : new Dictionary<string, string>(a.Metadata),
            },
            DurableToolDecision.SkipDecision s => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Skip,
                Message = s.SyntheticResult,
                Metadata = s.Metadata is null
                    ? null
                    : new Dictionary<string, string>(s.Metadata),
            },
            DurableToolDecision.BlockDecision b => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Block,
                Message = b.Reason,
                Metadata = b.Metadata is null
                    ? null
                    : new Dictionary<string, string>(b.Metadata),
            },
            _ => new DurableToolInterceptorResult { Outcome = DurableToolOutcome.Proceed },
        };


    /// <summary>The outcome decided by the interceptor.</summary>
    public DurableToolOutcome Outcome { get; init; }

    /// <summary>
    /// Human-readable description used when the outcome is <see cref="DurableToolOutcome.PauseForApproval"/>
    /// (the approval request description) or when <see cref="DurableToolOutcome.Proceed"/> and
    /// the interceptor supplied an enriched description for a <c>RequireApproval</c>-flagged tool.
    /// </summary>
    public string? EnrichedDescription { get; init; }

    /// <summary>
    /// Replacement argument dictionary when <see cref="Outcome"/> is
    /// <see cref="DurableToolOutcome.Proceed"/> and the interceptor requested argument substitution.
    /// </summary>
    public Dictionary<string, object?>? ModifiedArguments { get; init; }

    /// <summary>Key/value metadata carried for audit purposes. Present on all outcomes.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Semantic depends on <see cref="Outcome"/>:
    /// <list type="bullet">
    /// <item><see cref="DurableToolOutcome.Skip"/> — the synthetic result text injected as <c>FunctionResultContent</c>.</item>
    /// <item><see cref="DurableToolOutcome.Block"/> — the block reason injected as an error <c>FunctionResultContent</c>.</item>
    /// <item><see cref="DurableToolOutcome.PauseForApproval"/> — the human-readable description (same as <see cref="EnrichedDescription"/>).</item>
    /// <item>All other outcomes — <see langword="null"/>.</item>
    /// </list>
    /// </summary>
    public string? Message { get; init; }
}
