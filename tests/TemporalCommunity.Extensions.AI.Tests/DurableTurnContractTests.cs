using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Workflows;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public class DurableTurnContractTests
{
    [Fact]
    public void DispatchMode_HasFrozenNumericValuesAndSequentialDefault()
    {
        Assert.Equal(0, (int)DurableToolDispatchMode.Sequential);
        Assert.Equal(1, (int)DurableToolDispatchMode.Parallel);
        Assert.Equal(DurableToolDispatchMode.Sequential, new DurableTurnOptions().DispatchMode);

        var json = JsonSerializer.Serialize(
            new DurableTurnOptions { DispatchMode = DurableToolDispatchMode.Parallel },
            DurableAIJsonUtilities.DefaultOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("dispatchMode").GetInt32());

        var absent = JsonSerializer.Deserialize<DurableTurnOptions>(
            "{}",
            DurableAIJsonUtilities.DefaultOptions);
        Assert.Equal(DurableToolDispatchMode.Sequential, absent!.DispatchMode);
    }

    [Fact]
    public void CompletionReason_HasFrozenNumericValues()
    {
        Assert.Equal(0, (int)DurableTurnCompletionReason.FinalResponse);
        Assert.Equal(1, (int)DurableTurnCompletionReason.IterationLimitReached);
        Assert.Equal(
            "1",
            JsonSerializer.Serialize(
                DurableTurnCompletionReason.IterationLimitReached,
                DurableAIJsonUtilities.DefaultOptions));
    }

    [Fact]
    public void TypedTurnContracts_RoundTripApplicationTypes()
    {
        var request = new DurableTurnRequest<RequestData, TurnState>
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            RequestData = new RequestData("operation-1", 7),
            InitialTurnState = new TurnState("open", ["created"]),
            CorrelationId = "correlation",
            ConversationId = "conversation",
        };

        var json = JsonSerializer.Serialize(request, DurableAIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<DurableTurnRequest<RequestData, TurnState>>(
            json,
            DurableAIJsonUtilities.DefaultOptions);

        Assert.Equal(request.RequestData, restored!.RequestData);
        Assert.Equal(request.InitialTurnState!.Status, restored.InitialTurnState!.Status);
        Assert.Equal(request.InitialTurnState.Actions, restored.InitialTurnState.Actions);
        Assert.Equal("hello", restored.Messages[0].Text);
        Assert.Equal(DurableToolDispatchMode.Sequential, restored.Options.DispatchMode);
    }

    [Fact]
    public void SpecializedBase_IsAdditiveAndSealsManagedOrchestrationHooks()
    {
        var type = typeof(DurableToolWorkflowBase<RequestData, TurnState>);
        Assert.Equal(
            typeof(DurableChatWorkflowBase<DurableTurnResult<TurnState>>),
            type.BaseType);

        foreach (var methodName in new[]
        {
            "ExecuteTurnAsync",
            "BuildResponseEntry",
            "ApplyKeyedHistoryReducerAsync",
            "CreateContinueAsNewException",
        })
        {
            var method = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(method.IsFinal, $"{methodName} must be sealed.");
        }

        Assert.Equal(
            typeof(DurableChatWorkflowBase<ChatResponse>),
            typeof(LowLevelWorkflow).BaseType);
    }

    [Fact]
    public void CleanConsumerWorkflow_NeedsOnlyRunAndUpdateSurface()
    {
        Assert.False(typeof(ConsumerWorkflow).IsAbstract);
        var turn = typeof(ConsumerWorkflow).GetMethod(nameof(ConsumerWorkflow.TurnAsync))!;
        Assert.Equal(
            typeof(Task<DurableTurnResult<TurnState>>),
            turn.ReturnType);
    }

    private sealed record RequestData(string OperationId, int TenantNumber);

    private sealed record TurnState(string Status, IReadOnlyList<string> Actions);

    [Workflow("DurableTurnContractTests.ConsumerWorkflow")]
    private sealed class ConsumerWorkflow : DurableToolWorkflowBase<RequestData, TurnState>
    {
        [WorkflowRun]
        public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

        [WorkflowUpdate("Turn")]
        public Task<DurableTurnResult<TurnState>> TurnAsync(
            DurableTurnRequest<RequestData, TurnState> request) =>
            RunDurableTurnAsync(request);
    }

    private abstract class LowLevelWorkflow : DurableChatWorkflowBase<ChatResponse>
    {
        protected override DurableSessionResponse BuildResponseEntry(
            string correlationId,
            ChatResponse output,
            DateTimeOffset createdAt) => throw new NotImplementedException();

        protected override Task<ChatResponse> ExecuteTurnAsync(
            ActivityOptions activityOptions,
            DurableSessionRequest requestEntry,
            ChatOptions? chatOptions) => throw new NotImplementedException();

        protected override ContinueAsNewException CreateContinueAsNewException(
            DurableChatWorkflowInput input) => throw new NotImplementedException();
    }
}
