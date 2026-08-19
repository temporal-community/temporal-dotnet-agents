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

    private sealed class TestWorkflow : DurableChatWorkflowBase<ChatResponse>
    {
        public void Initialize(DurableChatWorkflowInput input) => InitializeInput(input);

        public DurableChatWorkflowInput GetRequiredInput() => RequiredInput;

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
