using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Workflows;
using DurableFunctionDeclarationSnapshot =
    global::TemporalCommunity.Extensions.AI.Internal.DurableFunctionDeclarationSnapshot;

namespace TemporalCommunity.Extensions.AI.Tests.Compat;

public sealed record TypedTurnRequestData(string OperationId);

public sealed record TypedTurnState(int Revision, IReadOnlyList<string> Receipts);

public sealed record TypedWorkflowConfiguration(
    string WorkflowType,
    string? HistoryReducerKey,
    IReadOnlyList<string> ToolsetIds,
    string? ManifestFingerprint,
    IReadOnlyList<string> ToolNames,
    int ToolMaximumAttempts,
    TimeSpan ToolTimeout,
    int MaxToolCallsPerTurn,
    int MaximumConsecutiveErrorsPerRequest,
    bool IncludeDetailedErrors,
    IReadOnlyList<string> RequiredApprovalTools,
    TimeSpan ApprovalTimeout);

[Workflow("TemporalCommunity.Extensions.AI.Tests.TypedDurableTurnWorkflow")]
public sealed class TypedDurableTurnWorkflow
    : DurableToolWorkflowBase<TypedTurnRequestData, TypedTurnState>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdate("Turn")]
    public Task<DurableTurnResult<TypedTurnState>> TurnAsync(
        DurableTurnRequest<TypedTurnRequestData, TypedTurnState> request) =>
        RunDurableTurnAsync(request);

    [WorkflowUpdate("TurnWithNullOptions")]
    public Task<DurableTurnResult<TypedTurnState>> TurnWithNullOptionsAsync(
        DurableTurnRequest<TypedTurnRequestData, TypedTurnState> request) =>
        RunDurableTurnAsync(new DurableTurnRequest<TypedTurnRequestData, TypedTurnState>
        {
            Messages = request.Messages,
            RequestData = request.RequestData,
            InitialTurnState = request.InitialTurnState,
            CorrelationId = request.CorrelationId,
            ConversationId = request.ConversationId,
            ChatOptions = request.ChatOptions,
            Options = null!,
        });

    [WorkflowUpdate("DoubleTurn")]
    public async Task DoubleTurnAsync(
        DurableTurnRequest<TypedTurnRequestData, TypedTurnState> request)
    {
        await RunDurableTurnAsync(request);
        await RunDurableTurnAsync(request);
    }

    [WorkflowUpdate("FailThenSecondTurn")]
    public async Task FailThenSecondTurnAsync(
        DurableTurnRequest<TypedTurnRequestData, TypedTurnState> request)
    {
        try
        {
            await RunDurableTurnAsync(request);
        }
        catch
        {
            // The same Update must remain consumed even when its first managed turn fails.
        }

        await RunDurableTurnAsync(request);
    }

    [WorkflowQuery("Configuration")]
    public TypedWorkflowConfiguration GetConfiguration()
    {
        var input = RequiredInput;
        var manifest = input.ToolsetManifest;
        var declaration = GetStateToolDeclaration(input);
        var manifestMember = manifest?.Members.Single(member => member.Declaration.Name == "state_tool");
        var toolOptions = manifestMember?.ToolActivityOptions
            ?? input.ToolActivityOptions![declaration.Name];
        return new TypedWorkflowConfiguration(
            Workflow.Info.WorkflowType,
            input.HistoryReducerKey,
            manifest?.ToolsetIds.ToArray() ?? [],
            manifest?.Fingerprint,
            manifest?.Members.Select(item => item.Declaration.Name).ToArray()
                ?? input.ToolDeclarations!.Select(item => item.Name).ToArray(),
            toolOptions.RetryPolicy!.MaximumAttempts,
            toolOptions.StartToCloseTimeout ?? TimeSpan.Zero,
            input.MaxToolCallsPerTurn,
            input.MaximumConsecutiveErrorsPerRequest,
            input.IncludeDetailedErrors,
            manifest?.Members.Where(member => member.RequiresApproval)
                .Select(member => member.Declaration.Name).ToArray()
                ?? input.RequiresApprovalTools?.ToArray()
                ?? [],
            input.ApprovalTimeout);
    }

    [WorkflowQuery("History")]
    public new IReadOnlyList<DurableSessionEntry> GetHistory() => History.ToArray();

    private static DurableFunctionDeclarationSnapshot GetStateToolDeclaration(
        DurableChatWorkflowInput input) =>
        input.ToolsetManifest?.Members
            .Select(member => member.Declaration)
            .SingleOrDefault(declaration => declaration.Name == "state_tool")
            ?? input.ToolDeclarations?.SingleOrDefault(declaration => declaration.Name == "state_tool")
            ?? throw new InvalidOperationException("The frozen state_tool declaration is missing.");
}
