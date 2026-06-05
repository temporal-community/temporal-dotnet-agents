namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Result of <see cref="AgentActivities.LoadAlwaysScopesAsync"/>.
/// </summary>
internal sealed class LoadAlwaysScopesResult
{
    /// <summary>
    /// The loaded always-scope records. Empty list (never <see langword="null"/>) when none found.
    /// </summary>
    public required IReadOnlyList<ApprovalScopeRecord> Scopes { get; init; }
}
