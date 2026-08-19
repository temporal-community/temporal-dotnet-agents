using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

[Workflow("Sample.WorkflowBackedMcpOperation")]
public sealed class WorkflowOperationWorkflow
{
    [WorkflowRun]
    public async Task<WorkflowOperationResult> RunAsync(WorkflowOperationInput input)
    {
        if (string.Equals(input.WorkItem, "wait", StringComparison.Ordinal))
        {
            await Workflow.WaitConditionAsync(() => false);
        }

        await Workflow.DelayAsync(TimeSpan.FromMilliseconds(250));
        if (string.Equals(input.WorkItem, "fail", StringComparison.Ordinal))
        {
            throw new ApplicationFailureException(
                "The sample operation failed.",
                errorType: "SampleOperationFailed",
                nonRetryable: true);
        }

        return new(input.OperationId, $"{input.WorkItem} completed for tenant {input.TenantId}");
    }
}
