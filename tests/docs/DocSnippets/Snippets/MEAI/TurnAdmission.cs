// HARNESS for docs/how-to/MEAI/usage.md § "Custom typed-turn admission".
using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace DocSnippets.MEAI;

// BEGIN SNIPPET docs/how-to/MEAI/usage.md#custom-typed-turn-admission
public sealed record ApplicationRequest(string OperationId);
public sealed record ApplicationTurnState(int Revision);

[Workflow("ApplicationWorkflow")]
public sealed class ApplicationWorkflow
    : DurableToolWorkflowBase<ApplicationRequest, ApplicationTurnState>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdateValidator(nameof(TurnAsync))]
    public void ValidateTurn(
        DurableTurnRequest<ApplicationRequest, ApplicationTurnState> request) =>
        ValidateDurableTurnAdmission(request);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ApplicationTurnState>> TurnAsync(
        DurableTurnRequest<ApplicationRequest, ApplicationTurnState> request) =>
        RunDurableTurnAsync(request);
}
// END SNIPPET docs/how-to/MEAI/usage.md#custom-typed-turn-admission
