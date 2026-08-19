using System.Collections.Concurrent;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

/// <summary>
/// Demo-only ledger. Use a durable, atomic application store when deduplication must outlive a
/// process or Temporal retention.
/// </summary>
public sealed class InMemoryWorkflowOperationLedger : IWorkflowOperationLedger
{
    private readonly ConcurrentDictionary<WorkflowOperationKey, WorkflowToolResult> entries = new();

    public bool TryGet(WorkflowOperationKey key, out WorkflowToolResult result) =>
        entries.TryGetValue(key, out result!);

    public void Store(WorkflowOperationKey key, WorkflowToolResult result) => entries[key] = result;
}
