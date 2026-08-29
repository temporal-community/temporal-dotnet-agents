using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using TemporalCommunity.Extensions.AI.Approvals;
using TemporalCommunity.Extensions.AI.Session;
using Xunit;

namespace TemporalCommunity.Extensions.AI.Tests;

public sealed class WorkflowInitializationTests
{
    [Fact]
    public void InitializeInput_MakesRequiredInputAvailableSynchronously()
    {
        var workflow = new TestWorkflow();
        var input = new DurableChatWorkflowInput { ApprovalTimeout = TimeSpan.FromMinutes(3) };

        workflow.Initialize(input);

        Assert.Same(input, workflow.GetRequiredInput());
    }

    [Fact]
    public void RequiredInput_WithoutInitialization_FailsLoudly()
    {
        var workflow = new TestWorkflow();

        var exception = Assert.Throws<InvalidOperationException>(workflow.GetRequiredInput);

        Assert.Contains("before RunAsync initialized Input", exception.Message);
    }

    [Fact]
    public async Task InvalidApprovalBody_FailsNonRetryablyBeforeWorkflowAwait()
    {
        var mixin = new DurableApprovalMixin();

        var exception = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            mixin.RequestApprovalAsync(
                new DurableApprovalRequest { RequestId = " " },
                TimeSpan.FromMinutes(1)));

        Assert.True(exception.NonRetryable);
        Assert.Equal(DurableApprovalMixin.InvalidRequestErrorType, exception.ErrorType);
    }

    [Fact]
    public void StripMessages_Response_PreservesCompletionMetadata()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = 12,
            ReasoningTokenCount = 7,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["provider.cached"] = 5,
            },
        };
        var original = new DurableSessionResponse
        {
            CorrelationId = "strip-metadata",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Messages = [new ChatMessage(ChatRole.Assistant, "diagnostic")],
            Usage = usage,
            FinishReason = ChatFinishReason.Length,
            CompletionReason = DurableTurnCompletionReason.IncompleteResponse,
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future"] = JsonDocument.Parse("17").RootElement.Clone(),
            },
        };
        var workflow = new TestWorkflow();

        var stripped = Assert.IsType<DurableSessionResponse>(workflow.Strip(original));

        Assert.Empty(stripped.Messages);
        Assert.Equal(original.CorrelationId, stripped.CorrelationId);
        Assert.Equal(original.CreatedAt, stripped.CreatedAt);
        Assert.Same(usage, stripped.Usage);
        Assert.Equal(ChatFinishReason.Length, stripped.FinishReason);
        Assert.Equal(DurableTurnCompletionReason.IncompleteResponse, stripped.CompletionReason);
        Assert.Equal(17, stripped.AdditionalProperties!["future"].GetInt32());
    }

    private sealed class TestWorkflow : DurableChatWorkflowBase<ChatResponse>
    {
        public void Initialize(DurableChatWorkflowInput input) => InitializeInput(input);

        public DurableChatWorkflowInput GetRequiredInput() => RequiredInput;

        public DurableSessionEntry Strip(DurableSessionEntry entry) =>
            StripMessagesFromEntry(entry);

        protected override DurableSessionResponse BuildResponseEntry(
            string correlationId,
            ChatResponse output,
            DateTimeOffset createdAt) => throw new NotSupportedException();

        protected override Task<ChatResponse> ExecuteTurnAsync(
            ActivityOptions activityOptions,
            DurableSessionRequest requestEntry,
            ChatOptions? chatOptions) => throw new NotSupportedException();

        protected override ContinueAsNewException CreateContinueAsNewException(
            DurableChatWorkflowInput input) => throw new NotSupportedException();
    }
}
