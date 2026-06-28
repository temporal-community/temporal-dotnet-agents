using System.Text.Json.Serialization;
using TemporalCommunity.Extensions.AI.Approvals;

namespace TemporalCommunity.Extensions.Agents.Workflows;

/// <summary>
/// Input for <see cref="AgentActivities.AppendAlwaysScopeAsync"/>.
/// </summary>
internal sealed class AppendAlwaysScopeInput
{
    /// <summary>The agent name (identifies the per-agent approval-scope store bucket).</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// The session ID (agent workflow ID). Used for activity-side logging and observability only;
    /// NOT forwarded to <see cref="TemporalCommunity.Extensions.Agents.Approvals.IApprovalScopeStore.AppendAsync"/>.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The logical store key (from <see cref="ApprovalScopesOptions.AlwaysScopesStoreKey"/>).
    /// Forwarded to the store as the second positional argument.
    /// </summary>
    public required string StoreKey { get; init; }

    /// <summary>The tool name that the scope covers.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Optional argument pattern. <see langword="null"/> means the scope covers any call of the tool
    /// regardless of arguments.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApprovalScopePattern? Pattern { get; init; }

    /// <summary>When the scope was granted (workflow-minted UTC timestamp via <c>Workflow.UtcNow</c>).</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>
    /// The <c>RequestId</c> of the approval that produced this scope.
    /// Used by the store for idempotent append-if-absent semantics.
    /// </summary>
    public required string OriginatingRequestId { get; init; }
}
