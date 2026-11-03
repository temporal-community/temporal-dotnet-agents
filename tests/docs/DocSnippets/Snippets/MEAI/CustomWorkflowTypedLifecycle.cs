using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;

namespace DocSnippets.MEAI;

// BEGIN SNIPPET docs/how-to/MEAI/custom-workflow-output.md#package-managed-typed-turns
public sealed record ReportRequest(string OperationId);
public sealed record ReportState(int Revision);

[Workflow("CustomWorkflow.Report")]
public sealed class CustomReportWorkflow
    : DurableToolWorkflowBase<ReportRequest, ReportState>
{
    protected override IReadOnlyList<string>? DurableToolsetBaselineIds =>
        ["catalog", "operations"];

    [WorkflowRun]
    public new async Task RunAsync(DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InitializeInput(input); // synchronously, before the first await
        await base.RunAsync(input).ConfigureAwait(true);
    }

    [WorkflowUpdateValidator(nameof(TurnAsync))]
    public void ValidateTurn(
        DurableTurnRequest<ReportRequest, ReportState> request) =>
        ValidateDurableTurnAdmission(request);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<ReportState>> TurnAsync(
        DurableTurnRequest<ReportRequest, ReportState> request) =>
        RunDurableTurnAsync(request);
}
// END SNIPPET docs/how-to/MEAI/custom-workflow-output.md#package-managed-typed-turns
