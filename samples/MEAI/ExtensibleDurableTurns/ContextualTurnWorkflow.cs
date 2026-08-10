using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace ExtensibleDurableTurns;

[Workflow("ExtensibleDurableTurns.ContextualTurnWorkflow")]
public sealed class ContextualTurnWorkflow
    : DurableToolWorkflowBase<ProcessingRequest, ProcessingState>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ProcessingState>> TurnAsync(
        DurableTurnRequest<ProcessingRequest, ProcessingState> request) =>
        RunDurableTurnAsync(request);
}
