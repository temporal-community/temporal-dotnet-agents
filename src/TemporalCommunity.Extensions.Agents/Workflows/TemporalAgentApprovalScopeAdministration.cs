using Temporalio.Client;
using TemporalCommunity.Extensions.Agents.Approvals;
using TemporalCommunity.Extensions.Agents.Session;

namespace TemporalCommunity.Extensions.Agents.Workflows;

internal sealed class TemporalAgentApprovalScopeAdministration(ITemporalClient client)
    : ITemporalAgentApprovalScopeAdministration
{
    public Task<SessionApprovalScopeGrantResult> GrantSessionScopeAsync(
        TemporalAgentSessionId sessionId,
        SessionApprovalScopeGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return handle.ExecuteUpdateAsync<AgentWorkflow, SessionApprovalScopeGrantResult>(
            workflow => workflow.GrantSessionApprovalScopeAsync(request),
            new WorkflowUpdateOptions
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });
    }

    public Task<bool> RevokeSessionScopeAsync(
        TemporalAgentSessionId sessionId,
        string grantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return handle.ExecuteUpdateAsync<AgentWorkflow, bool>(
            workflow => workflow.RevokeSessionApprovalScopeAsync(grantId),
            new WorkflowUpdateOptions
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });
    }
}
