using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// A persisted scope-approval record. Stored as a JSON array in session <see cref="Microsoft.Agents.AI.AgentSessionStateBag"/>
/// (session scope) or in the agent's configured <see cref="HistoryStore.IApprovalScopeStore"/>
/// (always scope).
/// </summary>
public sealed class ApprovalScopeRecord
{
    /// <summary>The tool name this scope covers.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Optional argument pattern. <see langword="null"/> = match any call of this tool.
    /// </summary>
    public ApprovalScopePattern? Pattern { get; init; }

    /// <summary>When the scope was granted. Used for audit and expiry calculations.</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>The RequestId of the approval that produced this scope.</summary>
    public required string OriginatingRequestId { get; init; }
}
