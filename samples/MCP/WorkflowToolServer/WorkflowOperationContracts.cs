namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

public sealed record WorkflowOperationInput(string TenantId, string OperationId, string WorkItem);

public sealed record WorkflowOperationResult(string OperationId, string Result);

public sealed record WorkflowToolResult(
    string OperationId,
    string Status,
    string? Result = null,
    string? ErrorCode = null);

public readonly record struct WorkflowOperationKey(string TenantId, string OperationId);

public interface IWorkflowOperationLedger
{
    bool TryGet(WorkflowOperationKey key, out WorkflowToolResult result);

    void Store(WorkflowOperationKey key, WorkflowToolResult result);
}
