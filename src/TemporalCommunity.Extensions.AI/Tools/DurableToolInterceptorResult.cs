using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalCommunity.Extensions.AI.Tools;

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
                    : p.ModifiedArguments.ToDictionary(kv => kv.Key, kv => kv.Value),
                Metadata = p.Metadata is null
                    ? null
                    : p.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            DurableToolDecision.ApprovalRequiredDecision a => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.PauseForApproval,
                EnrichedDescription = a.Description,
                Message = a.Description,
                Metadata = a.Metadata is null
                    ? null
                    : a.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            DurableToolDecision.SkipDecision s => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Skip,
                Message = s.SyntheticResult,
                Metadata = s.Metadata is null
                    ? null
                    : s.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            DurableToolDecision.BlockDecision b => new DurableToolInterceptorResult
            {
                Outcome = DurableToolOutcome.Block,
                Message = b.Reason,
                Metadata = b.Metadata is null
                    ? null
                    : b.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
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

    /// <summary>
    /// Serialized StateBag snapshot reflecting any mutations the interceptor made to the
    /// session state during <c>BeforeToolCallAsync</c>. <see langword="null"/> when the
    /// interceptor did not change the bag (the common case). The workflow merges this back
    /// into its carried StateBag after the interceptor activity returns and before tool
    /// dispatch, so interceptor-driven state changes are durable.
    /// </summary>
    /// <remarks>
    /// Optional and <c>[JsonIgnore(WhenWritingNull)]</c> so in-flight histories serialized
    /// before this field existed continue to replay (wire-compatible).
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedStateBag { get; init; }

    /// <summary>
    /// Returns a copy of this result with <see cref="UpdatedStateBag"/> set to
    /// <paramref name="updatedStateBag"/>. Used by the interceptor activity to attach a
    /// StateBag write-back without mutating the original instance.
    /// </summary>
    internal DurableToolInterceptorResult WithUpdatedStateBag(JsonElement updatedStateBag) =>
        new()
        {
            Outcome = this.Outcome,
            EnrichedDescription = this.EnrichedDescription,
            ModifiedArguments = this.ModifiedArguments,
            Metadata = this.Metadata,
            Message = this.Message,
            UpdatedStateBag = updatedStateBag,
        };
}
