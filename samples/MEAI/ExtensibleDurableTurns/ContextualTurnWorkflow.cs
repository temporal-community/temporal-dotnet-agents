using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace ExtensibleDurableTurns;

[Workflow("ExtensibleDurableTurns.ContextualTurnWorkflow")]
public sealed class ContextualTurnWorkflow
    : DurableToolWorkflowBase<ProcessingRequest, ProcessingState>
{
    // This workflow can never expand beyond these two worker-owned groups. The resolver records
    // their declarations and durable policies once at workflow start.
    protected override IReadOnlyList<string>? DurableToolsetBaselineIds =>
        ["reference", "processing"];

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ProcessingState>> TurnAsync(
        DurableTurnRequest<ProcessingRequest, ProcessingState> request) =>
        RunDurableTurnAsync(request);
}
