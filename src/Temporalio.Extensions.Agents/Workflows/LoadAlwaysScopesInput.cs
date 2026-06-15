namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input for <see cref="AgentActivities.LoadAlwaysScopesAsync"/>.
/// </summary>
internal sealed class LoadAlwaysScopesInput
{
    /// <summary>The agent name (identifies the per-agent approval-scope store bucket).</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// The logical store key (from <see cref="ApprovalScopesOptions.AlwaysScopesStoreKey"/>).
    /// </summary>
    public required string StoreKey { get; init; }
}
